using System.Security.Claims;
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
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

public static class AdminTaxiHandlers
{
    private const string VehicleTypesCacheKey = "taxi_vehicle_types";
    private static readonly TimeSpan VehicleTypesCacheTtl = TimeSpan.FromHours(24);
    private const string TaxiNotFound = "Taxi not found";
    private const string StatusAll = "All";

    private static IResult Concurrent() =>
        ConflictResponse("This taxi was just modified by another request. Please retry.", "CONCURRENT_UPDATE");

    private static AdminTaxiDto ToAdminTaxiDto(TaxiListing taxi)
    {
        var dto = taxi.Adapt<TaxiOwnerDto>().Adapt<AdminTaxiDto>();
        dto.UserId = taxi.UserId;
        dto.OwnerName = taxi.User?.Name;
        dto.OwnerPhone = taxi.User?.PhoneNumber;
        dto.IsDeleted = taxi.IsDeleted;
        return dto;
    }

    private static async Task<List<AdminTaxiDto>> WithReportCountsAsync(IReadOnlyList<TaxiListing> taxis, IUnitOfWork unitOfWork)
    {
        var counts = taxis.Count == 0
            ? new Dictionary<Guid, int>()
            : await unitOfWork.ListingReports.GetPendingCountsForListingsAsync(taxis.Select(t => t.Id).ToList(), ListingKinds.Taxi);
        return taxis.Select(t =>
        {
            var dto = ToAdminTaxiDto(t);
            dto.PendingReportCount = counts.GetValueOrDefault(t.Id);
            return dto;
        }).ToList();
    }

    public static async Task<IResult> GetTaxis(
        IUnitOfWork unitOfWork, int page = 1, int pageSize = 20, string status = TaxiStatuses.PendingReview)
    {
        if (status != StatusAll && !TaxiStatuses.All.Contains(status)) return BadRequestResponse("Invalid status");
        pageSize = Math.Clamp(pageSize, 1, 100);
        page = Math.Max(1, page);

        var result = await unitOfWork.Taxis.GetAdminPagedAsync(status, page, pageSize);
        var items = await WithReportCountsAsync(result.Items, unitOfWork);
        return OkResponse(new { items, hasMore = result.HasMore });
    }

    // Survives IsDeleted so a reported taxi can still be reviewed after it was deleted.
    public static async Task<IResult> GetTaxiById(Guid id, IUnitOfWork unitOfWork)
    {
        var taxi = await unitOfWork.Taxis.GetByIdForAdminAsync(id);
        if (taxi == null) return NotFoundResponse(TaxiNotFound);
        var items = await WithReportCountsAsync([taxi], unitOfWork);
        return OkResponse(items[0]);
    }

    private static NotificationEvent BuildTaxiNotification(Guid userId, string type, string title, string body) => new()
    {
        Id = Guid.NewGuid(),
        TargetUserId = userId,
        Type = type,
        Title = title,
        Body = body,
        ActionRoute = UserNotificationRoutes.MyTaxi,
        CreatedAt = DateTime.UtcNow,
    };

    private static Guid? GetAdminId(ClaimsPrincipal principal)
        => Guid.TryParse(principal.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var adminId) ? adminId : null;

    public static async Task<IResult> ApproveTaxi(
        Guid id, ClaimsPrincipal principal, IUnitOfWork unitOfWork, ApplicationDbContext db, IServiceProvider sp,
        IHubContext<NotificationHub> hubContext, IRabbitMqPublisher publisher)
    {
        var taxi = await unitOfWork.Taxis.GetByIdAsync(id);
        if (taxi == null || taxi.IsDeleted) return NotFoundResponse(TaxiNotFound);
        if (taxi.Status != TaxiStatuses.PendingReview)
            return BadRequestResponse("This request has already been resolved");

        var ownerActive = await db.Users.AnyAsync(u => u.Id == taxi.UserId && u.IsActive);

        await unitOfWork.BeginTransactionAsync();
        try
        {
            var now = DateTime.UtcNow;
            taxi.Status = TaxiStatuses.Approved;
            // A suspended driver's taxi stays hidden; un-suspend turns it back on.
            if (!ownerActive) taxi.IsAvailable = false;
            taxi.RejectedReason = null;
            taxi.ReviewedAt = now;
            taxi.ReviewedByAdminId = GetAdminId(principal);
            taxi.UpdatedAt = now;
            await unitOfWork.Taxis.UpdateAsync(taxi);

            var notification = BuildTaxiNotification(taxi.UserId, NotificationTypes.TaxiApproved,
                "Your taxi is now live", "Your taxi has been approved and is now live.");
            db.NotificationEvents.Add(notification);

            try
            {
                await unitOfWork.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                await unitOfWork.RollbackTransactionAsync();
                return Concurrent();
            }

            await unitOfWork.CommitTransactionAsync();

            await TaxiCache.InvalidateAsync(sp.GetService<IConnectionMultiplexer>(), taxi.DistrictId);

            await AdminHandlers.SendGoLiveNotificationAsync(hubContext, publisher, notification);

            return OkResponse(new { success = true });
        }
        catch
        {
            await unitOfWork.RollbackTransactionAsync();
            throw;
        }
    }

    // A PendingReview taxi was never in the public caches, so rejecting it busts nothing.
    public static async Task<IResult> RejectTaxi(
        Guid id, RejectGoLiveRequest request, IValidator<RejectGoLiveRequest> validator,
        ClaimsPrincipal principal, IUnitOfWork unitOfWork, ApplicationDbContext db,
        IHubContext<NotificationHub> hubContext, IRabbitMqPublisher publisher)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var taxi = await unitOfWork.Taxis.GetByIdAsync(id);
        if (taxi == null || taxi.IsDeleted) return NotFoundResponse(TaxiNotFound);
        if (taxi.Status != TaxiStatuses.PendingReview)
            return BadRequestResponse("This request has already been resolved");

        await unitOfWork.BeginTransactionAsync();
        try
        {
            var now = DateTime.UtcNow;
            taxi.Status = TaxiStatuses.Rejected;
            taxi.RejectedReason = request.Reason;
            taxi.ReviewedAt = now;
            taxi.ReviewedByAdminId = GetAdminId(principal);
            taxi.UpdatedAt = now;
            await unitOfWork.Taxis.UpdateAsync(taxi);

            var notification = BuildTaxiNotification(taxi.UserId, NotificationTypes.TaxiRejected,
                "Your taxi was not approved", $"Your taxi was rejected. Reason: {request.Reason}");
            db.NotificationEvents.Add(notification);

            try
            {
                await unitOfWork.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                await unitOfWork.RollbackTransactionAsync();
                return Concurrent();
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

    internal static void ApplyTaxiReportDetails(AdminListingReportDto dto, TaxiListing taxi)
    {
        dto.DistrictName = taxi.District?.Name;
        dto.CityName = taxi.City?.Name;
        dto.TaxiVehicleTypeName = taxi.VehicleType?.Name;
        dto.TaxiPlateDisplay = TaxiPlate.Format(taxi.NumberPlate);
        dto.TaxiSeats = taxi.Seats;
    }

    // Report takedown: the taxi goes back to Rejected, so the driver can fix it and resubmit for review.
    internal static Task<IResult> RejectReportedTaxiAsync(
        ListingReport report, string reason, Guid? adminId,
        IUnitOfWork unitOfWork, ApplicationDbContext db, IServiceProvider sp,
        IHubContext<NotificationHub> hubContext, IRabbitMqPublisher publisher)
        => TakeDownCoreAsync(report.ListingId, reason, adminId, report.Id, unitOfWork, db, sp, hubContext, publisher);

    // Admin unpublish of a live taxi: the same takedown as a report, with no report to resolve.
    public static async Task<IResult> UnpublishTaxi(
        Guid id, RejectGoLiveRequest request, IValidator<RejectGoLiveRequest> validator,
        ClaimsPrincipal principal, IUnitOfWork unitOfWork, ApplicationDbContext db, IServiceProvider sp,
        IHubContext<NotificationHub> hubContext, IRabbitMqPublisher publisher)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var taxi = await unitOfWork.Taxis.GetByIdAsync(id);
        if (taxi == null || taxi.IsDeleted) return NotFoundResponse(TaxiNotFound);
        if (taxi.Status != TaxiStatuses.Approved) return BadRequestResponse("Only a live taxi can be unpublished");

        return await TakeDownCoreAsync(id, request.Reason, GetAdminId(principal), null, unitOfWork, db, sp, hubContext, publisher);
    }

    private static async Task<IResult> TakeDownCoreAsync(
        Guid taxiId, string reason, Guid? adminId, Guid? reportId,
        IUnitOfWork unitOfWork, ApplicationDbContext db, IServiceProvider sp,
        IHubContext<NotificationHub> hubContext, IRabbitMqPublisher publisher)
    {
        var taxi = await unitOfWork.Taxis.GetByIdAsync(taxiId);
        if (taxi == null || taxi.IsDeleted) return NotFoundResponse(TaxiNotFound);

        await unitOfWork.BeginTransactionAsync();
        try
        {
            var now = DateTime.UtcNow;
            taxi.Status = TaxiStatuses.Rejected;
            taxi.RejectedReason = reason;
            taxi.ReviewedAt = now;
            taxi.ReviewedByAdminId = adminId;
            taxi.UpdatedAt = now;
            await unitOfWork.Taxis.UpdateAsync(taxi);

            var notification = BuildTaxiNotification(taxi.UserId, NotificationTypes.TaxiRejected,
                "Your taxi was taken down", $"Your taxi was taken down: {reason}");
            db.NotificationEvents.Add(notification);

            try
            {
                await unitOfWork.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                await unitOfWork.RollbackTransactionAsync();
                return Concurrent();
            }

            if (reportId.HasValue)
                await unitOfWork.ListingReports.ResolveAsync(reportId.Value, adminId, "PostRejected");

            await unitOfWork.CommitTransactionAsync();

            await TaxiCache.InvalidateAsync(sp.GetService<IConnectionMultiplexer>(), taxi.DistrictId);

            await AdminHandlers.SendGoLiveNotificationAsync(hubContext, publisher, notification);

            return OkResponse(new { success = true });
        }
        catch
        {
            await unitOfWork.RollbackTransactionAsync();
            throw;
        }
    }

    // Soft delete, same as the driver's DeleteMine: plate photo removed, pending reports auto-resolved.
    public static async Task<IResult> DeleteTaxi(
        Guid id, IUnitOfWork unitOfWork, IPhotoService photoService, IServiceProvider sp)
    {
        var taxi = await unitOfWork.Taxis.GetByIdAsync(id);
        if (taxi == null || taxi.IsDeleted) return NotFoundResponse(TaxiNotFound);

        await photoService.DeleteRoomPhotosAsync(taxi.UserId, taxi.Id);

        var now = DateTime.UtcNow;
        taxi.IsDeleted = true;
        taxi.DeletedAt = now;
        taxi.UpdatedAt = now;

        try { await unitOfWork.SaveChangesAsync(); }
        catch (DbUpdateConcurrencyException) { return Concurrent(); }

        await unitOfWork.ListingReports.AutoResolvePendingForListingAsync(taxi.Id, ListingKinds.Taxi);
        await TaxiCache.InvalidateAsync(sp.GetService<IConnectionMultiplexer>(), taxi.DistrictId);

        return NoContentResponse();
    }

    // ---- Vehicle types. The public GET (active only) feeds the driver's picker. ----

    private static List<TaxiVehicleTypeDto> Ordered(IEnumerable<TaxiVehicleType> types) => types
        .OrderBy(t => TaxiVehicleCategories.SortOf(t.Category))
        .ThenBy(t => t.SortOrder)
        .ThenBy(t => t.Name)
        .Select(t => t.Adapt<TaxiVehicleTypeDto>())
        .ToList();

    public static async Task<IResult> GetVehicleTypes(IUnitOfWork unitOfWork, IMemoryCache cache)
    {
        if (!cache.TryGetValue(VehicleTypesCacheKey, out List<TaxiVehicleTypeDto>? cached) || cached == null)
        {
            cached = Ordered(await unitOfWork.TaxiVehicleTypes.GetActiveAsync());
            cache.Set(VehicleTypesCacheKey, cached, VehicleTypesCacheTtl);
        }
        return OkResponse(cached);
    }

    // Admin list: every row, inactive included.
    public static async Task<IResult> GetAllVehicleTypes(IUnitOfWork unitOfWork)
        => OkResponse(Ordered(await unitOfWork.TaxiVehicleTypes.GetAllAsync()));

    public static async Task<IResult> CreateVehicleType(
        CreateTaxiVehicleTypeRequest request, IValidator<CreateTaxiVehicleTypeRequest> validator,
        IUnitOfWork unitOfWork, IMemoryCache cache, ApplicationDbContext db)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var newName = request.Name.Trim();
        if (await db.TaxiVehicleTypes.AnyAsync(t => t.Name.ToLower() == newName.ToLower()))
            return ConflictResponse("A vehicle type with this name already exists");

        var type = new TaxiVehicleType
        {
            Id = Guid.NewGuid(),
            Name = request.Name.Trim(),
            Category = request.Category.Trim(),
            DefaultSeats = request.DefaultSeats,
            SortOrder = request.SortOrder,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        };
        await unitOfWork.TaxiVehicleTypes.AddAsync(type);
        try { await unitOfWork.SaveChangesAsync(); }
        catch (DbUpdateException) { return ConflictResponse("A vehicle type with this name already exists"); }

        cache.Remove(VehicleTypesCacheKey);

        return CreatedResponse(type.Adapt<TaxiVehicleTypeDto>(), $"/api/v1/admin/taxis/vehicle-types/{type.Id}");
    }

    public static async Task<IResult> UpdateVehicleType(
        Guid id, UpdateTaxiVehicleTypeRequest request, IValidator<UpdateTaxiVehicleTypeRequest> validator,
        IUnitOfWork unitOfWork, IMemoryCache cache, ApplicationDbContext db)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var type = await unitOfWork.TaxiVehicleTypes.GetByIdAsync(id);
        if (type == null) return NotFoundResponse("Vehicle type not found");

        if (request.Name != null)
        {
            var renamed = request.Name.Trim();
            if (await db.TaxiVehicleTypes.AnyAsync(t => t.Id != id && t.Name.ToLower() == renamed.ToLower()))
                return ConflictResponse("A vehicle type with this name already exists");
            type.Name = renamed;
        }
        if (request.Category != null) type.Category = request.Category.Trim();
        if (request.DefaultSeats != null) type.DefaultSeats = request.DefaultSeats == 0 ? null : request.DefaultSeats;
        if (request.SortOrder != null) type.SortOrder = request.SortOrder.Value;
        if (request.IsActive != null) type.IsActive = request.IsActive.Value;

        await unitOfWork.TaxiVehicleTypes.UpdateAsync(type);
        try { await unitOfWork.SaveChangesAsync(); }
        catch (DbUpdateException) { return ConflictResponse("A vehicle type with this name already exists"); }

        cache.Remove(VehicleTypesCacheKey);

        return OkResponse(type.Adapt<TaxiVehicleTypeDto>());
    }

    // Seeded rows come back on the next start if deleted, so they can only be switched off. Custom rows
    // delete only when no taxi (deleted ones included, FK Restrict) references them.
    public static async Task<IResult> DeleteVehicleType(Guid id, IUnitOfWork unitOfWork, IMemoryCache cache, ApplicationDbContext db)
    {
        var type = await unitOfWork.TaxiVehicleTypes.GetByIdAsync(id);
        if (type == null) return NotFoundResponse("Vehicle type not found");

        if (TaxiVehicleTypeSeed.IsSeeded(id))
            return BadRequestResponse("Seeded vehicle types can only be switched off");
        if (await db.TaxiListings.AnyAsync(t => t.VehicleTypeId == id))
            return BadRequestResponse("Cannot delete a vehicle type used by taxis", TaxiErrorTypes.TaxiVehicleTypeInUse);

        await unitOfWork.TaxiVehicleTypes.DeleteAsync(type);
        await unitOfWork.SaveChangesAsync();

        cache.Remove(VehicleTypesCacheKey);

        return NoContentResponse();
    }
}
