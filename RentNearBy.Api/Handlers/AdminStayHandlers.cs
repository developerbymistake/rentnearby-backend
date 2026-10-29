using FluentValidation;
using Mapster;
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
using RentNearBy.Infrastructure.Services;
using StackExchange.Redis;
using System.Security.Claims;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

public static class AdminStayHandlers
{
    private const string StayTypesCacheKey = "stay_types";
    private static readonly TimeSpan StayTypesCacheTtl = TimeSpan.FromHours(24);

    private static AdminStayDto ToAdminStayDto(StayListing stay, bool includePhotos)
    {
        var dto = stay.Adapt<StayOwnerDto>().Adapt<AdminStayDto>();
        dto.UserId = stay.UserId;
        dto.OwnerName = stay.User?.Name;
        dto.OwnerPhone = stay.User?.PhoneNumber;
        if (!includePhotos) dto.Photos = new();
        return dto;
    }

    public static async Task<IResult> GetStays(
        IUnitOfWork unitOfWork, ApplicationDbContext db,
        int page = 1, int pageSize = 20, string status = StayStatuses.PendingReview)
    {
        if (!StayStatuses.All.Contains(status)) return BadRequestResponse("Invalid status");
        pageSize = Math.Clamp(pageSize, 1, 100);
        page = Math.Max(1, page);

        var result = await unitOfWork.Stays.GetAdminPagedAsync(status, page, pageSize);

        // The paged query has no Photos include; Step1Done/Percent need them.
        var ids = result.Items.Select(s => s.Id).ToList();
        var photos = await db.StayPhotos.AsNoTracking()
            .Where(p => ids.Contains(p.StayListingId))
            .ToListAsync();
        var photosByStay = photos.ToLookup(p => p.StayListingId);
        foreach (var stay in result.Items)
            stay.Photos = photosByStay[stay.Id].OrderBy(p => p.PhotoOrder).ToList();

        var items = result.Items.Select(s => ToAdminStayDto(s, includePhotos: false)).ToList();
        return OkResponse(new { items, hasMore = result.HasMore });
    }

    public static async Task<IResult> GetStayById(Guid id, IUnitOfWork unitOfWork)
    {
        var stay = await unitOfWork.Stays.GetByIdForAdminAsync(id);
        if (stay == null || stay.IsDeleted) return NotFoundResponse("Stay not found");
        return OkResponse(ToAdminStayDto(stay, includePhotos: true));
    }

    private static NotificationEvent BuildStayNotification(Guid userId, string type, string title, string body) => new()
    {
        Id = Guid.NewGuid(),
        TargetUserId = userId,
        Type = type,
        Title = title,
        Body = body,
        ActionRoute = UserNotificationRoutes.MyStays,
        CreatedAt = DateTime.UtcNow,
    };

    private static Guid? GetAdminId(ClaimsPrincipal principal)
        => Guid.TryParse(principal.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var adminId) ? adminId : null;

    public static async Task<IResult> ApproveStay(
        Guid id, ClaimsPrincipal principal, IUnitOfWork unitOfWork, ApplicationDbContext db, IServiceProvider sp,
        IPlanEntitlementService entitlements, IHubContext<NotificationHub> hubContext, IRabbitMqPublisher publisher)
    {
        var stay = await unitOfWork.Stays.GetByIdAsync(id);
        if (stay == null || stay.IsDeleted) return NotFoundResponse("Stay not found");
        if (stay.Status != StayStatuses.PendingReview)
            return BadRequestResponse("This request has already been resolved");

        var limits = await entitlements.GetLimitsAsync(stay.UserId, ListingKinds.Stay);

        var ownerActive = await db.Users.AnyAsync(u => u.Id == stay.UserId && u.IsActive);

        await unitOfWork.BeginTransactionAsync();
        try
        {
            var now = DateTime.UtcNow;
            stay.Status = StayStatuses.Approved;
            stay.EverApproved = true;
            stay.PriorityRank = limits.PriorityRank;
            stay.IsFeatured = limits.IsFeatured;
            // A suspended owner's stay stays hidden; un-suspend turns it back on.
            if (!ownerActive) stay.AcceptingGuests = false;
            stay.RejectedReason = null;
            stay.ReviewedAt = now;
            stay.ReviewedByAdminId = GetAdminId(principal);
            stay.UpdatedAt = now;
            await unitOfWork.Stays.UpdateAsync(stay);

            var notification = BuildStayNotification(stay.UserId, NotificationTypes.StayApproved,
                "Your stay is now live", "Your stay has been approved and is now live.");
            db.NotificationEvents.Add(notification);

            try
            {
                await unitOfWork.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                await unitOfWork.RollbackTransactionAsync();
                return ConflictResponse("This stay was just modified by another request. Please retry.", "CONCURRENT_UPDATE");
            }

            await unitOfWork.CommitTransactionAsync();

            await StayCache.InvalidateAsync(sp.GetService<IConnectionMultiplexer>(), stay.DistrictId);

            await AdminHandlers.SendGoLiveNotificationAsync(hubContext, publisher, notification);

            return OkResponse(new { success = true });
        }
        catch
        {
            await unitOfWork.RollbackTransactionAsync();
            throw;
        }
    }

    // A PendingReview stay was never in the public caches, so rejecting it busts nothing.
    public static async Task<IResult> RejectStay(
        Guid id, RejectGoLiveRequest request, IValidator<RejectGoLiveRequest> validator,
        ClaimsPrincipal principal, IUnitOfWork unitOfWork, ApplicationDbContext db,
        IHubContext<NotificationHub> hubContext, IRabbitMqPublisher publisher)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var stay = await unitOfWork.Stays.GetByIdAsync(id);
        if (stay == null || stay.IsDeleted) return NotFoundResponse("Stay not found");
        if (stay.Status != StayStatuses.PendingReview)
            return BadRequestResponse("This request has already been resolved");

        await unitOfWork.BeginTransactionAsync();
        try
        {
            var now = DateTime.UtcNow;
            stay.Status = StayStatuses.Rejected;
            stay.RejectedReason = request.Reason;
            stay.ReviewedAt = now;
            stay.ReviewedByAdminId = GetAdminId(principal);
            stay.UpdatedAt = now;
            await unitOfWork.Stays.UpdateAsync(stay);

            var notification = BuildStayNotification(stay.UserId, NotificationTypes.StayRejected,
                "Your stay was not approved", $"Your stay was rejected. Reason: {request.Reason}");
            db.NotificationEvents.Add(notification);

            try
            {
                await unitOfWork.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                await unitOfWork.RollbackTransactionAsync();
                return ConflictResponse("This stay was just modified by another request. Please retry.", "CONCURRENT_UPDATE");
            }

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

    internal static void ApplyStayReportDetails(AdminListingReportDto dto, StayListing stay)
    {
        dto.Address = stay.Address;
        dto.Description = stay.Description;
        dto.DistrictName = stay.District?.Name;
        dto.CityName = stay.City?.Name;
        dto.StayName = stay.Name;
        dto.StayTypeName = stay.StayType?.Name;
        dto.MinPricePerNight = stay.MinPricePerNight;
    }

    // Report takedown: the stay goes back to Rejected, so the owner can fix it and resubmit for review.
    internal static Task<IResult> RejectReportedStayAsync(
        ListingReport report, string reason, Guid? adminId,
        IUnitOfWork unitOfWork, ApplicationDbContext db, IServiceProvider sp,
        IHubContext<NotificationHub> hubContext, IRabbitMqPublisher publisher)
        => TakeDownCoreAsync(report.ListingId, reason, adminId, report.Id, unitOfWork, db, sp, hubContext, publisher);

    // Admin unpublish of a live stay: the same takedown as a report, with no report to resolve.
    public static async Task<IResult> UnpublishStay(
        Guid id, RejectGoLiveRequest request, IValidator<RejectGoLiveRequest> validator,
        ClaimsPrincipal principal, IUnitOfWork unitOfWork, ApplicationDbContext db, IServiceProvider sp,
        IHubContext<NotificationHub> hubContext, IRabbitMqPublisher publisher)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var stay = await unitOfWork.Stays.GetByIdAsync(id);
        if (stay == null || stay.IsDeleted) return NotFoundResponse("Stay not found");
        if (stay.Status != StayStatuses.Approved) return BadRequestResponse("Only a live stay can be unpublished");

        return await TakeDownCoreAsync(id, request.Reason, GetAdminId(principal), null, unitOfWork, db, sp, hubContext, publisher);
    }

    private static async Task<IResult> TakeDownCoreAsync(
        Guid stayId, string reason, Guid? adminId, Guid? reportId,
        IUnitOfWork unitOfWork, ApplicationDbContext db, IServiceProvider sp,
        IHubContext<NotificationHub> hubContext, IRabbitMqPublisher publisher)
    {
        var stay = await unitOfWork.Stays.GetByIdAsync(stayId);
        if (stay == null || stay.IsDeleted) return NotFoundResponse("Stay not found");

        await unitOfWork.BeginTransactionAsync();
        try
        {
            var now = DateTime.UtcNow;
            stay.Status = StayStatuses.Rejected;
            stay.RejectedReason = reason;
            stay.ReviewedAt = now;
            stay.ReviewedByAdminId = adminId;
            stay.UpdatedAt = now;
            await unitOfWork.Stays.UpdateAsync(stay);

            var notification = BuildStayNotification(stay.UserId, NotificationTypes.StayRejected,
                "Your stay was taken down", $"Your stay was taken down: {reason}");
            db.NotificationEvents.Add(notification);

            try
            {
                await unitOfWork.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                await unitOfWork.RollbackTransactionAsync();
                return ConflictResponse("This stay was just modified by another request. Please retry.", "CONCURRENT_UPDATE");
            }

            if (reportId.HasValue)
                await unitOfWork.ListingReports.ResolveAsync(reportId.Value, adminId, "PostRejected");

            await unitOfWork.CommitTransactionAsync();

            await StayCache.InvalidateAsync(sp.GetService<IConnectionMultiplexer>(), stay.DistrictId);

            await AdminHandlers.SendGoLiveNotificationAsync(hubContext, publisher, notification);

            return OkResponse(new { success = true });
        }
        catch
        {
            await unitOfWork.RollbackTransactionAsync();
            throw;
        }
    }

    // Soft delete, same as the owner's DeleteMine: photos removed, pending reports auto-resolved.
    public static async Task<IResult> DeleteStay(
        Guid id, IUnitOfWork unitOfWork, IPhotoService photoService, IServiceProvider sp)
    {
        var stay = await unitOfWork.Stays.GetByIdAsync(id);
        if (stay == null || stay.IsDeleted) return NotFoundResponse("Stay not found");

        await photoService.DeleteRoomPhotosAsync(stay.UserId, stay.Id);

        var now = DateTime.UtcNow;
        stay.IsDeleted = true;
        stay.DeletedAt = now;
        stay.UpdatedAt = now;

        try { await unitOfWork.SaveChangesAsync(); }
        catch (DbUpdateConcurrencyException)
        {
            return ConflictResponse("This stay was just modified by another request. Please retry.", "CONCURRENT_UPDATE");
        }

        await unitOfWork.StayJobs.CloseActiveForStayAsync(stay.Id);
        await unitOfWork.ListingReports.AutoResolvePendingForListingAsync(stay.Id, ListingKinds.Stay);
        await StayCache.InvalidateAsync(sp.GetService<IConnectionMultiplexer>(), stay.DistrictId);

        return NoContentResponse();
    }

    // ---- Stay types. GET is public: the Details grid and the Stays filter read it. ----

    public static async Task<IResult> GetStayTypes(IUnitOfWork unitOfWork, IMemoryCache cache)
    {
        if (!cache.TryGetValue(StayTypesCacheKey, out List<StayTypeDto>? cached) || cached == null)
        {
            var types = await unitOfWork.StayTypes.GetAllAsync();
            cached = types.OrderBy(t => t.SortOrder).ThenBy(t => t.Name).Select(t => t.Adapt<StayTypeDto>()).ToList();
            cache.Set(StayTypesCacheKey, cached, StayTypesCacheTtl);
        }
        return OkResponse(cached);
    }

    public static async Task<IResult> CreateStayType(
        CreateStayTypeRequest request, IValidator<CreateStayTypeRequest> validator, IUnitOfWork unitOfWork, IMemoryCache cache)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var stayType = new StayType { Id = Guid.NewGuid(), Name = request.Name.Trim(), Description = request.Description, SortOrder = request.SortOrder, CreatedAt = DateTime.UtcNow };
        await unitOfWork.StayTypes.AddAsync(stayType);
        try { await unitOfWork.SaveChangesAsync(); }
        catch (DbUpdateException) { return ConflictResponse("A stay type with this name already exists"); }

        cache.Remove(StayTypesCacheKey);

        return CreatedResponse(stayType.Adapt<StayTypeDto>(), $"/api/v1/admin/stays/types/{stayType.Id}");
    }

    public static async Task<IResult> UpdateStayType(
        Guid id, UpdateStayTypeRequest request, IValidator<UpdateStayTypeRequest> validator, IUnitOfWork unitOfWork, IMemoryCache cache)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var stayType = await unitOfWork.StayTypes.GetByIdAsync(id);
        if (stayType == null) return NotFoundResponse("Stay type not found");

        if (request.Name != null) stayType.Name = request.Name.Trim();
        if (request.Description != null) stayType.Description = request.Description;

        await unitOfWork.StayTypes.UpdateAsync(stayType);
        try { await unitOfWork.SaveChangesAsync(); }
        catch (DbUpdateException) { return ConflictResponse("A stay type with this name already exists"); }

        cache.Remove(StayTypesCacheKey);

        return OkResponse(stayType.Adapt<StayTypeDto>());
    }

    // StayListing and QuestionTemplate both hold a Restrict FK, so any referencing row blocks the delete.
    public static async Task<IResult> DeleteStayType(Guid id, IUnitOfWork unitOfWork, IMemoryCache cache, ApplicationDbContext db)
    {
        var stayType = await unitOfWork.StayTypes.GetByIdAsync(id);
        if (stayType == null) return NotFoundResponse("Stay type not found");

        if (await db.StayListings.AnyAsync(s => s.StayTypeId == id))
            return BadRequestResponse("Cannot delete a stay type used by stays");
        if (await db.QuestionTemplates.AnyAsync(q => q.StayTypeId == id))
            return BadRequestResponse("Cannot delete a stay type used by chat questions");

        await unitOfWork.StayTypes.DeleteAsync(stayType);
        await unitOfWork.SaveChangesAsync();

        cache.Remove(StayTypesCacheKey);

        return NoContentResponse();
    }
}
