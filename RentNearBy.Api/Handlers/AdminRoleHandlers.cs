using FluentValidation;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
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

    public static async Task<IResult> GetRoleRequests(
        ApplicationDbContext db, IRoleChangeGuardService guard,
        int page = 1, int pageSize = 20, string? status = RoleChangeRequestStatuses.Pending)
    {
        pageSize = Math.Clamp(pageSize, 1, 100);
        page = Math.Max(1, page);

        var query = db.RoleChangeRequests.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(status) && !status.Trim().Equals("All", StringComparison.OrdinalIgnoreCase))
        {
            var normalizedStatus = status.Trim().ToUpperInvariant();
            query = query.Where(r => r.Status == normalizedStatus);
        }

        var result = await query
            .OrderByDescending(r => r.RequestedAt)
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
                RoomListingCount = db.RoomListings.Count(l => l.UserId == r.UserId && !l.IsDeleted),
                PlotListingCount = db.PlotListings.Count(l => l.UserId == r.UserId && !l.IsDeleted),
            })
            .ToPagedResultAsync(page, pageSize);

        var blocking = await guard.GetBlockingCountsAsync(result.Items.Select(r => r.UserId).Distinct().ToList());
        foreach (var item in result.Items)
        {
            item.LiveRoomListingCount = blocking[item.UserId].Rooms;
            item.LivePlotListingCount = blocking[item.UserId].Plots;
        }

        return OkResponse(new { items = result.Items, hasMore = result.HasMore });
    }

    public static async Task<IResult> ApproveRoleRequest(
        Guid id, ClaimsPrincipal principal, IUnitOfWork unitOfWork, ApplicationDbContext db,
        IListingPurgeService listingPurge, IRoleChangeGuardService guard, ICreditWalletService wallet,
        IHubContext<NotificationHub> hubContext, IRabbitMqPublisher publisher, IServiceProvider sp)
    {
        var request = await unitOfWork.RoleChangeRequests.GetByIdAsync(id);
        if (request == null) return NotFoundResponse("Role request not found");
        if (request.Status != RoleChangeRequestStatuses.Pending)
            return ConflictResponse("This request has already been resolved.", "RoleRequestResolved");

        var user = await unitOfWork.Users.GetByIdAsync(request.UserId);
        if (user == null) return NotFoundResponse("User not found");

        var target = await unitOfWork.Roles.GetByIdAsync(request.ToRoleId);
        if (target == null) return BadRequestResponse("Unknown or inactive role.", "InvalidRole");

        var outcome = await ChangeUserRoleCoreAsync(
            user, target, GetAdminId(principal), request.Id, enforceLiveGuard: true,
            unitOfWork, db, listingPurge, guard, wallet, hubContext, publisher, sp);

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
        IListingPurgeService listingPurge, IRoleChangeGuardService guard, ICreditWalletService wallet,
        IHubContext<NotificationHub> hubContext, IRabbitMqPublisher publisher, IServiceProvider sp)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var user = await unitOfWork.Users.GetByIdAsync(id);
        if (user == null) return NotFoundResponse("User not found");

        var target = await unitOfWork.Roles.GetActiveByKeyAsync(request.Role.Trim().ToUpperInvariant());
        if (target == null) return BadRequestResponse("Unknown or inactive role.", "InvalidRole");
        if (target.Id == user.RoleId) return BadRequestResponse("User already has this role.", "RoleUnchanged");

        await ChangeUserRoleCoreAsync(
            user, target, GetAdminId(principal), approveRequestId: null, enforceLiveGuard: false,
            unitOfWork, db, listingPurge, guard, wallet, hubContext, publisher, sp);

        return OkResponse(new { id = user.Id, role = target.Key });
    }

    private static async Task<RoleChangeOutcome> ChangeUserRoleCoreAsync(
        User user, Role target, Guid? adminId, Guid? approveRequestId, bool enforceLiveGuard,
        IUnitOfWork unitOfWork, ApplicationDbContext db, IListingPurgeService listingPurge, IRoleChangeGuardService guard,
        ICreditWalletService wallet, IHubContext<NotificationHub> hubContext, IRabbitMqPublisher publisher, IServiceProvider sp)
    {
        var leftKind = UserRoles.ListingKindOf(user.RoleId);
        var targetKind = UserRoles.ListingKindOf(target.Id);
        var purgeKind = leftKind != null && leftKind != targetKind ? leftKind : null;
        IReadOnlyList<Guid> purgedListingIds = [];
        IReadOnlyList<Guid> purgedDistrictIds = [];

        await unitOfWork.BeginTransactionAsync();
        try
        {
            if (enforceLiveGuard && purgeKind != null
                && (await guard.GetBlockingCountsAsync(user.Id)).ForKind(purgeKind) > 0)
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
                await RefundPendingGoLiveCreditsAsync(user.Id, purgeKind, db, wallet);
                purgedDistrictIds = purgeKind == ListingKinds.Room
                    ? await db.RoomListings.Where(l => l.UserId == user.Id && !l.IsDeleted).Select(l => l.DistrictId).Distinct().ToListAsync()
                    : await db.PlotListings.Where(p => p.UserId == user.Id && !p.IsDeleted).Select(p => p.DistrictId).Distinct().ToListAsync();
                purgedListingIds = await listingPurge.DeleteUserListingRowsAsync(user.Id, purgeKind);
                await unitOfWork.ListingReports.AutoResolvePendingForListingsAsync(purgedListingIds, purgeKind);
                await unitOfWork.Notifications.DeleteByActionRouteForUserAsync(user.Id, UserNotificationRoutes.ForListingKind(purgeKind));
            }

            await unitOfWork.Users.SetRoleAsync(user.Id, target.Id);

            var notification = BuildRoleApprovedNotification(user.Id, target.Name);
            db.NotificationEvents.Add(notification);
            await unitOfWork.SaveChangesAsync();

            await unitOfWork.Sessions.RevokeAllUserSessionsAsync(user.Id);

            await unitOfWork.CommitTransactionAsync();

            if (purgeKind != null)
                await InvalidateListingCachesAsync(sp.GetService<IConnectionMultiplexer>(), purgeKind, purgedDistrictIds);

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

    // The wallet survives a role change, so credits spent on a never-approved Go-Live must be refunded before the purge.
    private static async Task RefundPendingGoLiveCreditsAsync(
        Guid userId, string listingKind, ApplicationDbContext db, ICreditWalletService wallet)
    {
        var refunds = listingKind == ListingKinds.Room
            ? await db.RoomListings
                .Where(l => l.UserId == userId && !l.IsDeleted
                    && l.LiveRequestStatus == GoLiveRequestStatuses.Pending && l.RequestedPlanCreditsSpent > 0)
                .Select(l => new { l.Id, Credits = l.RequestedPlanCreditsSpent!.Value })
                .ToListAsync()
            : await db.PlotListings
                .Where(p => p.UserId == userId && !p.IsDeleted
                    && p.LiveRequestStatus == GoLiveRequestStatuses.Pending && p.RequestedPlanCreditsSpent > 0)
                .Select(p => new { p.Id, Credits = p.RequestedPlanCreditsSpent!.Value })
                .ToListAsync();

        foreach (var refund in refunds)
            await wallet.AddCreditsAsync(userId, refund.Credits, CreditTransactionReasons.GoLivePendingRefund, refund.Id);
    }

    private static async Task InvalidateListingCachesAsync(IConnectionMultiplexer? redis, string listingKind, IReadOnlyList<Guid> districtIds)
    {
        if (redis == null) return;
        try
        {
            var cache = redis.GetDatabase();
            var server = redis.GetServers().FirstOrDefault(s => s.IsConnected);
            var isRoom = listingKind == ListingKinds.Room;
            await cache.KeyDeleteAsync(isRoom ? "home:recentRooms" : "home:recentPlots");
            foreach (var districtId in districtIds)
            {
                await cache.KeyDeleteAsync(isRoom ? $"home:forYouRooms:{districtId}" : $"home:forYouPlots:{districtId}");
                if (server == null) continue;
                var patterns = isRoom
                    ? new[] { $"nearby:{districtId}:*", $"nearest:{districtId}:*" }
                    : new[] { $"nearby_plot:{districtId}:*", $"nearest_plot:{districtId}:*" };
                foreach (var pattern in patterns)
                    await foreach (var key in server.KeysAsync(pattern: pattern))
                        await cache.KeyDeleteAsync(key);
            }
        }
        catch { }
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
