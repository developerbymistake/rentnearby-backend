using FluentValidation;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using RentNearBy.Api.Hubs;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Extensions;
using StackExchange.Redis;
using System.Security.Claims;
using Role = RentNearBy.Core.Entities.Role;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

public static class AdminRoleHandlers
{
    private enum RoleChangeOutcome { Changed, AlreadyResolved, BlockedByLiveListing }

    private static IResult RoleUnavailable(Role target) => ConflictResponse(
        $"{target.Name} is not available while its tab is off.", RoleHandlers.RoleUnavailableType);

    public static async Task<IResult> GetRoleRequests(
        ApplicationDbContext db, IRoleChangeGuardService guard, IUnitOfWork unitOfWork, IMemoryCache cache,
        int page = 1, int pageSize = 20, string? status = RoleChangeRequestStatuses.Pending, string? cursor = null)
    {
        pageSize = Math.Clamp(pageSize, 1, 100);
        page = Math.Max(1, page);

        var query = db.RoleChangeRequests.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(status) && !status.Trim().Equals("All", StringComparison.OrdinalIgnoreCase))
        {
            var normalizedStatus = status.Trim().ToUpperInvariant();
            query = query.Where(r => r.Status == normalizedStatus);
        }

        // cursor = keyset on (RequestedAt DESC, Id DESC), safe while approve/reject shrinks the queue;
        // page > 1 without a cursor = the old admin app's offset paging.
        var useOffset = string.IsNullOrEmpty(cursor) && page > 1;
        if (!string.IsNullOrEmpty(cursor))
        {
            if (!KeysetCursor.TryDecode(cursor, out var at, out var afterId)) return BadRequestResponse("Invalid cursor");
            query = query.Where(r => r.RequestedAt <= at && (r.RequestedAt < at || r.Id.CompareTo(afterId) < 0));
        }

        var rows = await query
            .OrderByDescending(r => r.RequestedAt)
            .ThenByDescending(r => r.Id)
            .Skip(useOffset ? (page - 1) * pageSize : 0)
            .Take(pageSize + 1)
            .Select(r => new AdminRoleChangeRequestDto
            {
                Id = r.Id,
                UserId = r.UserId,
                UserName = r.User.Name,
                UserPhone = r.User.PhoneNumber,
                FromRole = r.FromRole.Key,
                FromRoleName = r.FromRole.Name,
                ToRole = r.ToRole.Key,
                ToRoleName = r.ToRole.Name,
                Status = r.Status,
                RequestedAt = r.RequestedAt,
                ReviewedAt = r.ReviewedAt,
                RejectionReason = r.RejectionReason,
                StayListingCount = db.StayListings.Count(s => s.UserId == r.UserId && !s.IsDeleted),
                TaxiListingCount = db.TaxiListings.Count(t => t.UserId == r.UserId && !t.IsDeleted),
            })
            .ToListAsync();
        var hasMore = rows.Count > pageSize;
        var items = hasMore ? rows.Take(pageSize).ToList() : rows;
        var nextCursor = KeysetCursor.EncodeLast(items, hasMore, r => r.RequestedAt, r => r.Id);

        var blocking = await guard.GetBlockingCountsAsync(items.Select(r => r.UserId).Distinct().ToList());
        var availableRoleKeys = (await ConfigHandlers.GetAvailableRolesAsync(unitOfWork, cache)).Select(r => r.Key).ToHashSet();
        foreach (var item in items)
        {
            var counts = blocking[item.UserId];
            item.LiveStayListingCount = counts.Stays;
            item.LiveTaxiListingCount = counts.Taxis;
            item.ToRoleAvailable = availableRoleKeys.Contains(item.ToRole);
        }

        return OkResponse(new { items, hasMore, nextCursor });
    }

    public static async Task<IResult> ApproveRoleRequest(
        Guid id, ClaimsPrincipal principal, IUnitOfWork unitOfWork, ApplicationDbContext db,
        IListingPurgeService listingPurge, IRoleChangeGuardService guard, IPlanPurchaseService purchases,
        IHubContext<NotificationHub> hubContext, IRabbitMqPublisher publisher, IServiceProvider sp, IMemoryCache cache)
    {
        var request = await unitOfWork.RoleChangeRequests.GetByIdAsync(id);
        if (request == null) return NotFoundResponse("Role request not found");
        if (request.Status != RoleChangeRequestStatuses.Pending)
            return ConflictResponse("This request has already been resolved.", "RoleRequestResolved");

        var user = await unitOfWork.Users.GetByIdAsync(request.UserId);
        if (user == null) return NotFoundResponse("User not found");

        var target = await unitOfWork.Roles.GetByIdAsync(request.ToRoleId);
        if (target == null) return BadRequestResponse("Unknown or inactive role.", "InvalidRole");

        // Stays PENDING: approvable again once the role's tab is back on.
        if (!TabState.IsRoleAvailable(await ConfigHandlers.GetAppTabsCachedAsync(unitOfWork, cache), target))
            return RoleUnavailable(target);

        var outcome = await ChangeUserRoleCoreAsync(
            user, target, GetAdminId(principal), request.Id, enforceLiveGuard: true,
            unitOfWork, db, listingPurge, guard, purchases, hubContext, publisher, sp);

        return outcome switch
        {
            RoleChangeOutcome.Changed => OkResponse(new { success = true }),
            RoleChangeOutcome.BlockedByLiveListing => ConflictResponse(
                RoleChangeRules.LiveListingBlockedMessage(UserRoles.ListingKindOf(user.RoleId)!), RoleChangeRules.LiveListingErrorType),
            _ => ConflictResponse("This request has already been resolved.", "RoleRequestResolved"),
        };
    }

    public static async Task<IResult> RejectRoleRequest(
        Guid id, RejectRoleChangeRequest request, IValidator<RejectRoleChangeRequest> validator,
        ClaimsPrincipal principal, IUnitOfWork unitOfWork, ApplicationDbContext db,
        IHubContext<NotificationHub> hubContext, IRabbitMqPublisher publisher)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var roleRequest = await unitOfWork.RoleChangeRequests.GetByIdAsync(id);
        if (roleRequest == null) return NotFoundResponse("Role request not found");
        if (roleRequest.Status != RoleChangeRequestStatuses.Pending)
            return ConflictResponse("This request has already been resolved.", "RoleRequestResolved");

        var target = await unitOfWork.Roles.GetByIdAsync(roleRequest.ToRoleId);
        if (target == null) return BadRequestResponse("Unknown or inactive role.", "InvalidRole");

        var adminId = GetAdminId(principal);

        await unitOfWork.BeginTransactionAsync();
        try
        {
            var rows = await unitOfWork.RoleChangeRequests.ResolveIfPendingAsync(
                roleRequest.Id, RoleChangeRequestStatuses.Rejected, adminId, request.Reason);
            if (rows == 0)
            {
                await unitOfWork.RollbackTransactionAsync();
                return ConflictResponse("This request has already been resolved.", "RoleRequestResolved");
            }

            var notification = BuildRoleRejectedNotification(roleRequest.UserId, target.Name, request.Reason);
            db.NotificationEvents.Add(notification);
            await unitOfWork.SaveChangesAsync();

            await unitOfWork.CommitTransactionAsync();

            await AdminHandlers.SendGoLiveNotificationAsync(hubContext, publisher, notification);

            return OkResponse(new { success = true });
        }
        catch
        {
            await unitOfWork.RollbackTransactionAsync();
            throw;
        }
    }

    public static async Task<IResult> UpdateUserRole(
        Guid id, RoleKeyRequest request, IValidator<RoleKeyRequest> validator,
        ClaimsPrincipal principal, IUnitOfWork unitOfWork, ApplicationDbContext db,
        IListingPurgeService listingPurge, IRoleChangeGuardService guard, IPlanPurchaseService purchases,
        IHubContext<NotificationHub> hubContext, IRabbitMqPublisher publisher, IServiceProvider sp, IMemoryCache cache)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var user = await unitOfWork.Users.GetByIdAsync(id);
        if (user == null) return NotFoundResponse("User not found");

        var target = await unitOfWork.Roles.GetActiveByKeyAsync(request.Role.Trim().ToUpperInvariant());
        if (target == null) return BadRequestResponse("Unknown or inactive role.", "InvalidRole");
        if (target.Id == user.RoleId) return BadRequestResponse("User already has this role.", "RoleUnchanged");
        if (!TabState.IsRoleAvailable(await ConfigHandlers.GetAppTabsCachedAsync(unitOfWork, cache), target))
            return RoleUnavailable(target);

        await ChangeUserRoleCoreAsync(
            user, target, GetAdminId(principal), approveRequestId: null, enforceLiveGuard: false,
            unitOfWork, db, listingPurge, guard, purchases, hubContext, publisher, sp);

        return OkResponse(new { id = user.Id, role = target.Key });
    }

    private static async Task<RoleChangeOutcome> ChangeUserRoleCoreAsync(
        User user, Role target, Guid? adminId, Guid? approveRequestId, bool enforceLiveGuard,
        IUnitOfWork unitOfWork, ApplicationDbContext db, IListingPurgeService listingPurge, IRoleChangeGuardService guard,
        IPlanPurchaseService purchases, IHubContext<NotificationHub> hubContext, IRabbitMqPublisher publisher, IServiceProvider sp)
    {
        var leftKind = UserRoles.ListingKindOf(user.RoleId);
        var targetKind = UserRoles.ListingKindOf(target.Id);
        var purgeKind = leftKind != null && leftKind != targetKind ? leftKind : null;
        IReadOnlyList<Guid> purgedListingIds = [];
        IReadOnlyList<Guid> purgedDistrictIds = [];
        IReadOnlyList<TripClosurePush> tripPushes = [];
        var tripClosure = sp.GetRequiredService<ITripEnquiryClosure>();

        await unitOfWork.BeginTransactionAsync();
        try
        {
            // Owner lock first: a concurrent plan grant either commits before (and is cancelled here) or
            // runs after and sees the new role (Paid + RoleMismatch, auto refund).
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Users\" WHERE \"Id\" = {user.Id} FOR UPDATE");

            if (enforceLiveGuard && purgeKind != null
                && await guard.GetBlockingCountAsync(user.Id, purgeKind) > 0)
            {
                await unitOfWork.RollbackTransactionAsync();
                return RoleChangeOutcome.BlockedByLiveListing;
            }

            if (approveRequestId is Guid requestId)
            {
                var rows = await unitOfWork.RoleChangeRequests.ResolveIfPendingAsync(
                    requestId, RoleChangeRequestStatuses.Approved, adminId, null);
                if (rows == 0)
                {
                    await unitOfWork.RollbackTransactionAsync();
                    return RoleChangeOutcome.AlreadyResolved;
                }
            }
            else
            {
                await unitOfWork.RoleChangeRequests.ResolvePendingForUserAsync(
                    user.Id, target.Id, adminId, "Role changed by admin");
            }

            if (purgeKind != null)
            {
                // The leaving feature's plan ends with the role (no refund); open orders expire.
                await purchases.CancelActivePlansAsync(user.Id, purgeKind, "Role changed");
                purgedDistrictIds = purgeKind switch
                {
                    ListingKinds.Taxi => await db.TaxiListings.Where(t => t.UserId == user.Id && !t.IsDeleted && t.DistrictId != null).Select(t => t.DistrictId!.Value).Distinct().ToListAsync(),
                    ListingKinds.Stay => await db.StayListings.Where(s => s.UserId == user.Id && !s.IsDeleted && s.DistrictId != null).Select(s => s.DistrictId!.Value).Distinct().ToListAsync(),
                    _ => throw new InvalidOperationException($"Unknown listing kind '{purgeKind}'"),
                };
                purgedListingIds = await listingPurge.DeleteUserListingRowsAsync(user.Id, purgeKind);
                if (purgeKind is ListingKinds.Stay or ListingKinds.Taxi)
                    tripPushes = await tripClosure.OnListingsRemovedAsync(purgeKind, purgedListingIds);
                await unitOfWork.ListingReports.AutoResolvePendingForListingsAsync(purgedListingIds, purgeKind);
                foreach (var route in UserNotificationRoutes.OwnedByKind(purgeKind))
                    await unitOfWork.Notifications.DeleteByActionRouteForUserAsync(user.Id, route);
            }

            await unitOfWork.Users.SetRoleAsync(user.Id, target.Id);

            var notification = BuildRoleApprovedNotification(user.Id, target.Name);
            db.NotificationEvents.Add(notification);
            await unitOfWork.SaveChangesAsync();

            await unitOfWork.Sessions.RevokeAllUserSessionsAsync(user.Id);

            await unitOfWork.CommitTransactionAsync();

            if (purgeKind != null)
                await InvalidateListingCachesAsync(sp.GetService<IConnectionMultiplexer>(), purgeKind, purgedDistrictIds);

            await tripClosure.AfterCommitAsync(tripPushes);
            await listingPurge.DeleteListingPhotosBestEffortAsync(user.Id, purgedListingIds);

            await AdminHandlers.SendGoLiveNotificationAsync(hubContext, publisher, notification);

            return RoleChangeOutcome.Changed;
        }
        catch
        {
            await unitOfWork.RollbackTransactionAsync();
            throw;
        }
    }

    private static async Task InvalidateListingCachesAsync(IConnectionMultiplexer? redis, string listingKind, IReadOnlyList<Guid> districtIds)
    {
        if (redis == null) return;
        if (listingKind == ListingKinds.Stay)
        {
            foreach (var districtId in districtIds)
                await StayCache.InvalidateAsync(redis, districtId);
            return;
        }
        if (listingKind == ListingKinds.Taxi)
        {
            foreach (var districtId in districtIds)
                await TaxiCache.InvalidateAsync(redis, districtId);
        }
    }

    private static Guid? GetAdminId(ClaimsPrincipal principal)
        => Guid.TryParse(principal.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var adminId) ? adminId : null;

    private static NotificationEvent BuildRoleApprovedNotification(Guid userId, string roleName) => new()
    {
        Id = Guid.NewGuid(),
        TargetUserId = userId,
        Type = NotificationTypes.RoleRequestApproved,
        Title = "Role updated",
        Body = $"Your role is now {roleName}. Please log in again.",
        ActionRoute = UserNotificationRoutes.Notifications,
        CreatedAt = DateTime.UtcNow,
    };

    private static NotificationEvent BuildRoleRejectedNotification(Guid userId, string roleName, string reason) => new()
    {
        Id = Guid.NewGuid(),
        TargetUserId = userId,
        Type = NotificationTypes.RoleRequestRejected,
        Title = "Role request rejected",
        Body = $"Your request to become {roleName} was rejected. Reason: {reason}",
        ActionRoute = UserNotificationRoutes.Notifications,
        CreatedAt = DateTime.UtcNow,
    };
}
