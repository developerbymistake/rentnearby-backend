using FluentValidation;
using Mapster;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NetTopologySuite.Features;
using RentNearBy.Api.Hubs;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Extensions;
using RentNearBy.Infrastructure.Services;
using StackExchange.Redis;
using static RentNearBy.Api.Extensions.ApiResults;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RentNearBy.Api.Handlers;

public static class AdminHandlers
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);

    public static async Task<IResult> GetDistricts(IUnitOfWork unitOfWork, IMemoryCache cache)
    {
        if (!cache.TryGetValue("districts", out List<DistrictDto>? cached) || cached == null)
        {
            var districts = await unitOfWork.Districts.GetAllAsync();
            cached = districts.Select(d => d.Adapt<DistrictDto>()).ToList();
            cache.Set("districts", cached, CacheTtl);
        }
        return OkResponse(cached);
    }

    // Simplification tolerance in degrees for the boundary the client renders as a map overlay — the
    // full-fidelity District.Boundary column stays untouched and remains the authoritative ST_Contains gate
    // at /listings/context and /plots/context.
    private const double BoundarySimplifyToleranceDegrees = 0.0005;

    public static async Task<IResult> GetDistrictBoundary(Guid id, ApplicationDbContext db, IMemoryCache cache)
    {
        var cacheKey = $"district_boundary_{id}";
        if (!cache.TryGetValue(cacheKey, out FeatureCollection? cached) || cached == null)
        {
            var row = await db.Districts
                .Where(d => d.Id == id && d.IsActive)
                .Select(d => new { d.Id, d.Name, d.Boundary })
                .FirstOrDefaultAsync();

            if (row == null || row.Boundary == null)
                return NotFoundResponse("District boundary not found");

            var simplified = NetTopologySuite.Simplify.TopologyPreservingSimplifier.Simplify(
                row.Boundary, BoundarySimplifyToleranceDegrees);

            var attributes = new AttributesTable();
            attributes.Add("districtId", row.Id);
            attributes.Add("name", row.Name);
            var feature = new Feature(simplified, attributes);
            cached = new FeatureCollection { feature };
            cache.Set(cacheKey, cached, CacheTtl);
        }
        return OkResponse(cached);
    }

    public record ToggleDistrictRequest(bool IsActive);

    private static async Task FlushContextCacheAsync(IConnectionMultiplexer? redis)
    {
        if (redis == null) return;
        try
        {
            var server = redis.GetServers().FirstOrDefault(s => s.IsConnected);
            if (server == null) return;
            var redisDb = redis.GetDatabase();
            await foreach (var key in server.KeysAsync(pattern: "context:*"))
                await redisDb.KeyDeleteAsync(key);
        }
        catch { /* best-effort: TTL (10 min) covers Redis failures */ }
    }

    public static async Task<IResult> ToggleDistrictActive(
        Guid id, ToggleDistrictRequest request,
        ApplicationDbContext db, IMemoryCache cache, IServiceProvider sp)
    {
        var district = await db.Districts.FindAsync(id);
        if (district == null) return NotFoundResponse("District not found");

        district.IsActive = request.IsActive;
        await db.SaveChangesAsync();
        cache.Remove("states");
        cache.Remove("districts");
        cache.Remove($"cities_{id}");
        cache.Remove("cities_all");
        cache.Remove($"district_boundary_{id}");
        await FlushContextCacheAsync(sp.GetService<IConnectionMultiplexer>());

        return OkResponse(new { success = true, isActive = district.IsActive });
    }

    public static async Task<IResult> CreateDistrict(
        CreateDistrictRequest request,
        IValidator<CreateDistrictRequest> validator,
        ApplicationDbContext db,
        IGeocodingService geocoding,
        IMemoryCache cache,
        IServiceProvider sp)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var trimmedName = request.Name.Trim();
        var trimmedState = request.StateName.Trim();

        var exists = await db.Districts.AnyAsync(d =>
            d.Name.ToLower() == trimmedName.ToLower() &&
            d.StateName.ToLower() == trimmedState.ToLower());
        if (exists)
            return BadRequestResponse($"District '{trimmedName}' already exists in {trimmedState}.", "DuplicateDistrict");

        var boundary = await geocoding.FetchDistrictBoundaryAsync(trimmedName, trimmedState);

        var district = new District
        {
            Id = Guid.NewGuid(),
            Name = trimmedName,
            StateName = trimmedState,
            IsActive = false,
            Boundary = boundary,
            CreatedAt = DateTime.UtcNow,
        };

        db.Districts.Add(district);
        await db.SaveChangesAsync();

        cache.Remove("states");
        cache.Remove("districts");
        await FlushContextCacheAsync(sp.GetService<IConnectionMultiplexer>());

        return CreatedResponse(district.Adapt<DistrictDto>(), $"/api/v1/admin/districts/{district.Id}");
    }

    public static async Task<IResult> GetStates(ApplicationDbContext db, IMemoryCache cache)
    {
        if (!cache.TryGetValue("states", out List<StateDto>? cached) || cached == null)
        {
            cached = await db.Districts
                .GroupBy(d => d.StateName)
                .Select(g => new StateDto
                {
                    Name = g.Key,
                    TotalDistricts = g.Count(),
                    ActiveDistricts = g.Count(d => d.IsActive),
                })
                .OrderBy(s => s.Name)
                .ToListAsync();
            cache.Set("states", cached, CacheTtl);
        }
        return OkResponse(cached);
    }

    public static async Task<IResult> BulkToggleStateActive(
        string stateName, ToggleDistrictRequest request,
        ApplicationDbContext db, IMemoryCache cache, IServiceProvider sp)
    {
        var affectedIds = await db.Districts
            .Where(d => d.StateName == stateName)
            .Select(d => d.Id)
            .ToListAsync();

        var updated = await db.Districts
            .Where(d => d.StateName == stateName)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.IsActive, request.IsActive));

        if (updated == 0)
            return NotFoundResponse($"No districts found for state '{stateName}'");

        cache.Remove("states");
        cache.Remove("districts");
        cache.Remove("cities_all");
        foreach (var districtId in affectedIds)
            cache.Remove($"district_boundary_{districtId}");
        await FlushContextCacheAsync(sp.GetService<IConnectionMultiplexer>());

        return OkResponse(new { success = true, isActive = request.IsActive, updatedCount = updated });
    }

    public static async Task<IResult> ForceActivateDistrict(
        Guid id, ApplicationDbContext db, IMemoryCache cache, IServiceProvider sp)
    {
        var district = await db.Districts.FindAsync(id);
        if (district == null) return NotFoundResponse("District not found");
        district.IsActive = true;
        await db.SaveChangesAsync();
        cache.Remove("states");
        cache.Remove("districts");
        cache.Remove($"cities_{id}");
        cache.Remove("cities_all");
        cache.Remove($"district_boundary_{id}");
        await FlushContextCacheAsync(sp.GetService<IConnectionMultiplexer>());
        return OkResponse(new { success = true, isActive = true });
    }

    public static async Task<IResult> DeleteDistrict(Guid id, IUnitOfWork unitOfWork, IMemoryCache cache, ApplicationDbContext db, IServiceProvider sp)
    {
        var district = await unitOfWork.Districts.GetByIdAsync(id);
        if (district == null) return NotFoundResponse("District not found");

        if (await db.RoomListings.AnyAsync(l => l.DistrictId == id && l.IsActive && !l.IsDeleted))
            return BadRequestResponse("Cannot delete district with active listings");

        // StayListing.DistrictId is FK Restrict, so any stay row (deleted ones too) blocks the delete.
        if (await db.StayListings.AnyAsync(s => s.DistrictId == id))
            return BadRequestResponse("Cannot delete district with stays");

        // TaxiListing.DistrictId is FK Restrict too.
        if (await db.TaxiListings.AnyAsync(t => t.DistrictId == id))
            return BadRequestResponse("Cannot delete district with taxis");

        await unitOfWork.Districts.DeleteAsync(district);
        await unitOfWork.SaveChangesAsync();

        cache.Remove("states");
        cache.Remove("districts");
        cache.Remove($"cities_{id}");
        cache.Remove("cities_all");
        cache.Remove($"district_boundary_{id}");
        await FlushContextCacheAsync(sp.GetService<IConnectionMultiplexer>());

        return NoContentResponse();
    }

    public static async Task<IResult> GetCities(Guid? districtId, IUnitOfWork unitOfWork, IMemoryCache cache)
    {
        var cacheKey = districtId.HasValue ? $"cities_{districtId}" : "cities_all";
        if (!cache.TryGetValue(cacheKey, out List<CityDto>? cached) || cached == null)
        {
            IEnumerable<City> cities = districtId.HasValue
                ? await unitOfWork.Cities.GetByDistrictIdAsync(districtId.Value)
                : await unitOfWork.Cities.GetAllAsync();
            cached = cities.Select(c => c.Adapt<CityDto>()).ToList();
            cache.Set(cacheKey, cached, CacheTtl);
        }
        return OkResponse(cached);
    }

    public static async Task<IResult> CreateCity(CreateCityRequest request, IValidator<CreateCityRequest> validator, IUnitOfWork unitOfWork, IGeocodingService geocoding, IMemoryCache cache)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var trimmedName = request.Name.Trim();
        var exists = await unitOfWork.Cities.GetByDistrictIdAsync(request.DistrictId);
        if (exists.Any(c => string.Equals(c.Name, trimmedName, StringComparison.OrdinalIgnoreCase)))
            return BadRequestResponse($"City '{trimmedName}' already exists in this district", "DuplicateCity");

        decimal lat, lng;
        if (request.Latitude.HasValue && request.Longitude.HasValue)
        {
            lat = request.Latitude.Value;
            lng = request.Longitude.Value;
        }
        else
        {
            var district = await unitOfWork.Districts.GetByIdAsync(request.DistrictId);
            if (district is null) return NotFoundResponse("District not found");

            var point = await geocoding.GeocodeAsync($"{trimmedName}, {district.Name}, India");
            if (point is null)
                return BadRequestResponse($"Could not geocode '{trimmedName}'. Provide coordinates manually.");
            lat = point.Latitude;
            lng = point.Longitude;
        }

        var city = new City { Id = Guid.NewGuid(), DistrictId = request.DistrictId, Name = trimmedName, Latitude = lat, Longitude = lng, CreatedAt = DateTime.UtcNow };
        await unitOfWork.Cities.AddAsync(city);
        await unitOfWork.SaveChangesAsync();

        cache.Remove($"cities_{request.DistrictId}");
        cache.Remove("cities_all");

        return CreatedResponse(city.Adapt<CityDto>(), $"/api/v1/admin/cities/{city.Id}");
    }

    public static async Task<IResult> Geocode(string q, IGeocodingService geocoding)
    {
        if (string.IsNullOrWhiteSpace(q))
            return BadRequestResponse("Query parameter 'q' is required");

        var point = await geocoding.GeocodeAsync(q.Trim());
        if (point is null)
            return NotFoundResponse("No results found for the given query");

        return OkResponse(new { latitude = point.Latitude, longitude = point.Longitude, displayName = point.DisplayName });
    }

    public static async Task<IResult> DeleteCity(Guid id, IUnitOfWork unitOfWork, IMemoryCache cache, ApplicationDbContext db)
    {
        var city = await unitOfWork.Cities.GetByIdAsync(id);
        if (city == null) return NotFoundResponse("City not found");

        if (await db.RoomListings.AnyAsync(l => l.CityId == id && l.IsActive))
            return BadRequestResponse("Cannot delete city with active listings");

        await unitOfWork.Cities.DeleteAsync(city);
        await unitOfWork.SaveChangesAsync();

        cache.Remove($"cities_{city.DistrictId}");
        cache.Remove("cities_all");

        return NoContentResponse();
    }

    public static async Task<IResult> GetRoomTypes(IUnitOfWork unitOfWork, IMemoryCache cache)
    {
        if (!cache.TryGetValue("room_types", out List<RoomTypeDto>? cached) || cached == null)
        {
            var types = await unitOfWork.RoomTypes.GetAllAsync();
            cached = types.OrderBy(r => r.SortOrder).Select(r => r.Adapt<RoomTypeDto>()).ToList();
            cache.Set("room_types", cached, CacheTtl);
        }
        return OkResponse(cached);
    }

    public static async Task<IResult> CreateRoomType(CreateRoomTypeRequest request, IValidator<CreateRoomTypeRequest> validator, IUnitOfWork unitOfWork, IMemoryCache cache)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var roomType = new RoomType { Id = Guid.NewGuid(), Name = request.Name.Trim(), Description = request.Description, SortOrder = request.SortOrder, CreatedAt = DateTime.UtcNow };
        await unitOfWork.RoomTypes.AddAsync(roomType);
        await unitOfWork.SaveChangesAsync();

        cache.Remove("room_types");

        return CreatedResponse(roomType.Adapt<RoomTypeDto>(), $"/api/v1/admin/room-types/{roomType.Id}");
    }

    public static async Task<IResult> UpdateRoomType(Guid id, UpdateRoomTypeRequest request, IValidator<UpdateRoomTypeRequest> validator, IUnitOfWork unitOfWork, IMemoryCache cache)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var roomType = await unitOfWork.RoomTypes.GetByIdAsync(id);
        if (roomType == null) return NotFoundResponse("Room type not found");

        if (request.Name != null) roomType.Name = request.Name.Trim();
        if (request.Description != null) roomType.Description = request.Description;

        await unitOfWork.RoomTypes.UpdateAsync(roomType);
        await unitOfWork.SaveChangesAsync();

        cache.Remove("room_types");

        return OkResponse(roomType.Adapt<RoomTypeDto>());
    }

    public static async Task<IResult> DeleteRoomType(Guid id, IUnitOfWork unitOfWork, IMemoryCache cache, ApplicationDbContext db)
    {
        var roomType = await unitOfWork.RoomTypes.GetByIdAsync(id);
        if (roomType == null) return NotFoundResponse("Room type not found");

        if (await db.RoomListings.AnyAsync(l => l.RoomTypeId == id && l.IsActive))
            return BadRequestResponse("Cannot delete room type with active listings");

        await unitOfWork.RoomTypes.DeleteAsync(roomType);
        await unitOfWork.SaveChangesAsync();

        cache.Remove("room_types");

        return NoContentResponse();
    }

    // ── PlotListing Type CRUD ────────────────────────────────────────────────────────

    public static async Task<IResult> GetPlotTypes(IUnitOfWork unitOfWork, IMemoryCache cache)
    {
        if (!cache.TryGetValue("plot_types", out List<PlotTypeDto>? cached) || cached == null)
        {
            var types = await unitOfWork.PlotTypes.GetAllAsync();
            cached = types.OrderBy(p => p.SortOrder).Select(p => p.Adapt<PlotTypeDto>()).ToList();
            cache.Set("plot_types", cached, CacheTtl);
        }
        return OkResponse(cached);
    }

    public static async Task<IResult> CreatePlotType(CreatePlotTypeRequest request, IValidator<CreatePlotTypeRequest> validator, IUnitOfWork unitOfWork, IMemoryCache cache)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var plotType = new PlotType { Id = Guid.NewGuid(), Name = request.Name.Trim(), Description = request.Description, SortOrder = request.SortOrder, CreatedAt = DateTime.UtcNow };
        await unitOfWork.PlotTypes.AddAsync(plotType);
        await unitOfWork.SaveChangesAsync();

        cache.Remove("plot_types");

        return CreatedResponse(plotType.Adapt<PlotTypeDto>(), $"/api/v1/admin/plot-types/{plotType.Id}");
    }

    public static async Task<IResult> UpdatePlotType(Guid id, UpdatePlotTypeRequest request, IValidator<UpdatePlotTypeRequest> validator, IUnitOfWork unitOfWork, IMemoryCache cache)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var plotType = await unitOfWork.PlotTypes.GetByIdAsync(id);
        if (plotType == null) return NotFoundResponse("PlotListing type not found");

        if (request.Name != null) plotType.Name = request.Name.Trim();
        if (request.Description != null) plotType.Description = request.Description;

        await unitOfWork.PlotTypes.UpdateAsync(plotType);
        await unitOfWork.SaveChangesAsync();

        cache.Remove("plot_types");

        return OkResponse(plotType.Adapt<PlotTypeDto>());
    }

    public static async Task<IResult> DeletePlotType(Guid id, IUnitOfWork unitOfWork, IMemoryCache cache, ApplicationDbContext db)
    {
        var plotType = await unitOfWork.PlotTypes.GetByIdAsync(id);
        if (plotType == null) return NotFoundResponse("PlotListing type not found");

        if (await db.PlotListings.AnyAsync(p => p.PlotTypeId == id && p.IsActive && !p.IsDeleted))
            return BadRequestResponse("Cannot delete plot type with active plots");

        await unitOfWork.PlotTypes.DeleteAsync(plotType);
        await unitOfWork.SaveChangesAsync();

        cache.Remove("plot_types");

        return NoContentResponse();
    }

    public static async Task<IResult> GetReportReasons(IUnitOfWork unitOfWork, IMemoryCache cache)
    {
        if (!cache.TryGetValue("report_reasons", out List<ReportReasonDto>? cached) || cached == null)
        {
            var reasons = await unitOfWork.ReportReasons.GetAllAsync();
            cached = reasons.OrderBy(r => r.SortOrder).Select(r => r.Adapt<ReportReasonDto>()).ToList();
            cache.Set("report_reasons", cached, CacheTtl);
        }
        return OkResponse(cached);
    }

    public static async Task<IResult> CreateReportReason(CreateReportReasonRequest request, IValidator<CreateReportReasonRequest> validator, IUnitOfWork unitOfWork, IMemoryCache cache)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var reason = new ReportReason { Id = Guid.NewGuid(), Name = request.Name.Trim(), Description = request.Description, SortOrder = request.SortOrder, CreatedAt = DateTime.UtcNow };
        await unitOfWork.ReportReasons.AddAsync(reason);
        await unitOfWork.SaveChangesAsync();

        cache.Remove("report_reasons");

        return CreatedResponse(reason.Adapt<ReportReasonDto>(), $"/api/v1/admin/report-reasons/{reason.Id}");
    }

    public static async Task<IResult> UpdateReportReason(Guid id, UpdateReportReasonRequest request, IValidator<UpdateReportReasonRequest> validator, IUnitOfWork unitOfWork, IMemoryCache cache)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var reason = await unitOfWork.ReportReasons.GetByIdAsync(id);
        if (reason == null) return NotFoundResponse("Report reason not found");

        if (request.Name != null) reason.Name = request.Name.Trim();
        if (request.Description != null) reason.Description = request.Description;

        await unitOfWork.ReportReasons.UpdateAsync(reason);
        await unitOfWork.SaveChangesAsync();

        cache.Remove("report_reasons");

        return OkResponse(reason.Adapt<ReportReasonDto>());
    }

    public static async Task<IResult> DeleteReportReason(Guid id, IUnitOfWork unitOfWork, IMemoryCache cache, ApplicationDbContext db)
    {
        var reason = await unitOfWork.ReportReasons.GetByIdAsync(id);
        if (reason == null) return NotFoundResponse("Report reason not found");

        if (DataSeeder.IsSeededReportReason(id))
            return BadRequestResponse("Built-in reason. Rename it instead");

        if (await db.ListingReports.AnyAsync(r => r.ReasonId == id))
            return BadRequestResponse("Cannot delete a reason that is referenced by existing reports");

        await unitOfWork.ReportReasons.DeleteAsync(reason);
        await unitOfWork.SaveChangesAsync();

        cache.Remove("report_reasons");

        return NoContentResponse();
    }

    public static async Task<IResult> GetStats(ApplicationDbContext db)
    {
        var totalUsers = await db.Users.CountAsync();
        var totalListings = await db.RoomListings.CountAsync();
        var activeListings = await db.RoomListings.CountAsync(l => l.IsActive);
        var totalPlotListings = await db.PlotListings.CountAsync(l => !l.IsDeleted);
        var activePlotListings = await db.PlotListings.CountAsync(l => l.IsActive && !l.IsDeleted);
        var activeDistricts = await db.Districts.CountAsync(d => d.IsActive);
        var listingsByDistrict = await db.RoomListings
            .Where(l => l.IsActive)
            .GroupBy(l => new { l.DistrictId, DistrictName = l.District.Name })
            .Select(g => new { District = g.Key.DistrictName, Count = g.Count() })
            .ToListAsync();

        // Revenue = granted Razorpay plan purchases (refunded ones drop out).
        var now = DateTime.UtcNow;
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var revenue = db.PlanPurchases.Where(p => p.Status == PurchaseStatuses.Granted && p.Provider == PaymentProviders.Razorpay);
        var totalEarnings = await revenue.SumAsync(p => (int?)p.PayableInr) ?? 0;
        var currentMonthEarnings = await revenue.Where(p => p.GrantedAt >= monthStart).SumAsync(p => (int?)p.PayableInr) ?? 0;

        return OkResponse(new AdminStatsDto
        {
            TotalUsers = totalUsers,
            TotalListings = totalListings,
            ActiveListings = activeListings,
            TotalPlotListings = totalPlotListings,
            ActivePlotListings = activePlotListings,
            ActiveDistricts = activeDistricts,
            RoomListingsByDistrict = listingsByDistrict.ToDictionary(x => x.District, x => x.Count),
            TotalEarnings = totalEarnings,
            CurrentMonthEarnings = currentMonthEarnings,
        });
    }

    // ── Payment Kill Switch ─────────────────────────────────────────────────────
    // Single global row (RentNearBy.Core.Models.AppFeatureKeys.PaymentEnabled) in the generic
    // AppFeatureFlag table; also holds the free-mode listing limit.

    public static async Task<IResult> GetPaymentFeature(IUnitOfWork unitOfWork)
    {
        var flag = await unitOfWork.AppFeatureFlags.GetByKeyAsync(AppFeatureKeys.PaymentEnabled);
        if (flag == null) return NotFoundResponse("Payment feature flag not found");

        return OkResponse(ToPaymentFeatureDto(flag));
    }

    private static object ToPaymentFeatureDto(AppFeatureFlag flag) => new
    {
        isEnabled = flag.IsEnabled,
        freeDurationDays = flag.FreeDurationDays ?? 30,
        freeModeListingLimit = flag.FreeModeListingLimit,
        updatedByAdminId = flag.UpdatedByAdminId,
        updatedAt = flag.UpdatedAt,
        reason = flag.Reason,
    };

    // Flipping the flag or the free-mode limit re-applies listing limits (over-limit rooms/plots are plan-paused, newest kept).
    public static async Task<IResult> UpdatePaymentFeature(
        UpdatePaymentFeatureRequest request, IValidator<UpdatePaymentFeatureRequest> validator,
        ClaimsPrincipal principal, IUnitOfWork unitOfWork, IMemoryCache cache, IPaymentFeatureReader paymentReader,
        ILiveSlotService slots, IListingCacheInvalidator invalidator, ILoggerFactory loggerFactory)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var flag = await unitOfWork.AppFeatureFlags.GetByKeyAsync(AppFeatureKeys.PaymentEnabled);
        if (flag == null) return NotFoundResponse("Payment feature flag not found");

        var wasEnabled = flag.IsEnabled;
        var wasLimit = flag.FreeModeListingLimit;

        if (request.IsEnabled.HasValue) flag.IsEnabled = request.IsEnabled.Value;
        if (request.FreeDurationDays.HasValue) flag.FreeDurationDays = request.FreeDurationDays.Value;
        if (request.ClearFreeModeListingLimit) flag.FreeModeListingLimit = null;
        else if (request.FreeModeListingLimit.HasValue) flag.FreeModeListingLimit = request.FreeModeListingLimit.Value;
        if (request.Reason != null) flag.Reason = request.Reason;

        var adminIdClaim = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        flag.UpdatedByAdminId = Guid.TryParse(adminIdClaim, out var parsedAdminId) ? parsedAdminId : null;
        flag.UpdatedAt = DateTime.UtcNow;

        await unitOfWork.AppFeatureFlags.UpdateAsync(flag);
        await unitOfWork.SaveChangesAsync();

        paymentReader.Invalidate();
        ConfigHandlers.BustPlanDerivedConfig(cache);

        if (flag.IsEnabled != wasEnabled || flag.FreeModeListingLimit != wasLimit)
        {
            var logger = loggerFactory.CreateLogger(nameof(AdminHandlers));
            foreach (var kind in new[] { ListingKinds.Room, ListingKinds.Plot })
            {
                try
                {
                    var r = await slots.EnforceLimitsAsync(kind);
                    if (r.Paused > 0)
                    {
                        logger.LogInformation("Payment flag change plan-paused {Rows} {Kind} listings of {Owners} owners",
                            r.Paused, kind, r.Owners.Count);
                        await invalidator.BustForUsersAsync(r.Owners, kind);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Limit re-apply after payment flag change failed ({Kind}); the daily reconcile will repair it", kind);
                }
            }
        }

        return OkResponse(ToPaymentFeatureDto(flag));
    }

    // ── App Tabs (master table for the consumer app's bottom-nav) ───────────────
    // Seeded rows (RentNearBy.Core.Models.AppTabKeys) — renaming applies to any row; deactivating applies to the
    // five business tabs (Home/Profile are structural, rejected below). An off tab's consumer route group is
    // rejected by Filters/TabGateFilter.cs, its Home sections come back null and its linked role
    // (AppTabKeys.LinkedRoleKey) is hidden from new choices; existing holders keep the role in the DB.

    public static async Task<IResult> GetAppTabs(IUnitOfWork unitOfWork)
    {
        var tabs = await unitOfWork.AppTabs.GetAllAsync();
        return OkResponse(tabs.OrderBy(t => t.SortOrder).Select(ToAdminAppTabDto));
    }

    public static async Task<IResult> UpdateAppTab(
        string tabKey, UpdateAppTabRequest request, IValidator<UpdateAppTabRequest> validator,
        ClaimsPrincipal principal, IUnitOfWork unitOfWork, IMemoryCache cache)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var tab = await unitOfWork.AppTabs.GetByKeyAsync(tabKey);
        if (tab == null) return NotFoundResponse($"No app tab for key '{tabKey}'");

        if (request.IsActive == false && AppTabKeys.NonDeactivatable.Contains(tabKey))
            return BadRequestResponse($"'{tab.DisplayName}' is a structural tab and cannot be deactivated");

        if (request.DisplayName != null) tab.DisplayName = request.DisplayName;
        if (request.IsActive.HasValue) tab.IsActive = request.IsActive.Value;
        if (request.Reason != null) tab.Reason = request.Reason;

        var adminIdClaim = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        tab.UpdatedByAdminId = Guid.TryParse(adminIdClaim, out var parsedAdminId) ? parsedAdminId : null;
        tab.UpdatedAt = DateTime.UtcNow;

        await unitOfWork.AppTabs.UpdateAsync(tab);

        // Write-through: invalidate before the commit and again after it, then re-read and set. Each invalidate
        // bumps the cache version, so any reader that started before the commit skips its cache write.
        AppTabCache.Invalidate(cache);
        await unitOfWork.SaveChangesAsync();
        AppTabCache.Invalidate(cache);
        await AppTabCache.RefreshAsync(unitOfWork, cache);

        return OkResponse(ToAdminAppTabDto(tab));
    }

    private static AdminAppTabDto ToAdminAppTabDto(AppTab t) => new()
    {
        Id = t.Id, TabKey = t.TabKey, DisplayName = t.DisplayName, IsActive = t.IsActive, SortOrder = t.SortOrder,
        CanDeactivate = !AppTabKeys.NonDeactivatable.Contains(t.TabKey),
        ApiGated = AppTabKeys.ApiGated.Contains(t.TabKey),
        LinkedRoleKey = AppTabKeys.LinkedRoleKey(t.TabKey),
        UpdatedByAdminId = t.UpdatedByAdminId, UpdatedAt = t.UpdatedAt, Reason = t.Reason,
    };

    // ── App Version / Updates ───────────────────────────────────────────────────
    // Reason describes this change only, so it is overwritten on every PUT (unlike payment-feature).

    public static async Task<IResult> GetAppVersionConfigs(IUnitOfWork unitOfWork)
    {
        var rows = await unitOfWork.AppVersionConfigs.GetAllWithAdminAsync();
        return OkResponse(rows
            .OrderBy(r => Array.IndexOf(AppPlatforms.All, r.Platform))
            .Select(r => ToAdminAppVersionDto(r, r.UpdatedByAdmin))
            .ToList());
    }

    public static async Task<IResult> UpdateAppVersionConfig(
        string platform, UpdateAppVersionConfigRequest request, IValidator<UpdateAppVersionConfigRequest> validator,
        ClaimsPrincipal principal, IUnitOfWork unitOfWork, IMemoryCache cache)
    {
        if (!AppPlatforms.TryNormalize(platform, out var key))
            return BadRequestResponse("platform must be 'android' or 'ios'");

        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var row = await unitOfWork.AppVersionConfigs.GetByPlatformAsync(key);
        if (row == null) return NotFoundResponse($"No app version config for platform '{key}'");

        var wasEnabled = row.IsEnabled;
        var previousMin = row.MinBuildNumber;
        var previousLatest = row.LatestBuildNumber;

        if (request.IsEnabled.HasValue) row.IsEnabled = request.IsEnabled.Value;
        if (request.MinBuildNumber.HasValue) row.MinBuildNumber = request.MinBuildNumber.Value;
        if (request.MinVersionName != null) row.MinVersionName = request.MinVersionName;
        if (request.LatestBuildNumber.HasValue) row.LatestBuildNumber = request.LatestBuildNumber.Value;
        if (request.LatestVersionName != null) row.LatestVersionName = request.LatestVersionName;
        if (request.StoreUrl != null) row.StoreUrl = string.IsNullOrWhiteSpace(request.StoreUrl) ? null : request.StoreUrl.Trim();
        if (request.Message != null) row.Message = string.IsNullOrWhiteSpace(request.Message) ? null : request.Message.Trim();
        if (request.ForceCacheMaxAgeHours.HasValue) row.ForceCacheMaxAgeHours = request.ForceCacheMaxAgeHours.Value;
        row.Reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();

        if (row.MinBuildNumber > row.LatestBuildNumber)
            return BadRequestResponse("Minimum build cannot be greater than latest build", "VERSION_RANGE_INVALID");

        if (row.StoreUrl != null)
        {
            var expectedHost = AppPlatforms.StoreHosts[key];
            if (!Uri.TryCreate(row.StoreUrl, UriKind.Absolute, out var storeUri)
                || storeUri.Scheme != Uri.UriSchemeHttps
                || !string.Equals(storeUri.Host, expectedHost, StringComparison.OrdinalIgnoreCase))
                return BadRequestResponse($"Store URL for {key} must be an https link on {expectedHost}");

            if (key == AppPlatforms.Android
                && (!string.Equals(storeUri.AbsolutePath.TrimEnd('/'), AppPlatforms.AndroidStoreDetailsPath, StringComparison.OrdinalIgnoreCase)
                    || !QueryHelpers.ParseQuery(storeUri.Query).TryGetValue("id", out var packageIds)
                    || packageIds.Count != 1
                    || !string.Equals(packageIds[0], AppPlatforms.AndroidPackageId, StringComparison.Ordinal)))
                return BadRequestResponse($"Store URL for {key} must be {AppPlatforms.AndroidStoreUrl}");
        }

        if (row.IsEnabled && row.StoreUrl == null)
            return BadRequestResponse("Store URL is required while update checks are enabled", "STORE_URL_REQUIRED");

        if (row.IsEnabled && request.ConfirmUnseenBuild != true)
        {
            var ceiling = Math.Max(row.MaxSeenBuild, AppVersionRules.FirstGatedBuild);
            var enabling = !wasEnabled;

            if ((enabling || row.MinBuildNumber != previousMin) && row.MinBuildNumber > ceiling)
                return BadRequestResponse(
                    $"Minimum build {row.MinBuildNumber} has not been reported by any installed app yet (highest seen: {row.MaxSeenBuild}). Install it from the store first.",
                    "BUILD_NOT_SEEN");

            if ((enabling || row.LatestBuildNumber != previousLatest) && row.LatestBuildNumber > ceiling)
                return BadRequestResponse(
                    $"Latest build {row.LatestBuildNumber} has not been reported by any installed app yet (highest seen: {row.MaxSeenBuild}). Install it from the store first.",
                    "BUILD_NOT_SEEN");
        }

        var adminIdClaim = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        row.UpdatedByAdminId = Guid.TryParse(adminIdClaim, out var parsedAdminId) ? parsedAdminId : null;
        row.UpdatedAt = DateTime.UtcNow;

        await unitOfWork.SaveChangesAsync();

        cache.Remove(ConfigHandlers.AppVersionCacheKey(key));

        var admin = row.UpdatedByAdminId.HasValue ? await unitOfWork.Admins.GetByIdAsync(row.UpdatedByAdminId.Value) : null;
        return OkResponse(ToAdminAppVersionDto(row, admin));
    }

    private static AdminAppVersionConfigDto ToAdminAppVersionDto(AppVersionConfig row, Admin? updatedBy) => new()
    {
        Id = row.Id,
        Platform = row.Platform,
        IsEnabled = row.IsEnabled,
        MinBuildNumber = row.MinBuildNumber,
        MinVersionName = row.MinVersionName,
        LatestBuildNumber = row.LatestBuildNumber,
        LatestVersionName = row.LatestVersionName,
        StoreUrl = row.StoreUrl,
        Message = row.Message,
        ForceCacheMaxAgeHours = row.ForceCacheMaxAgeHours,
        MaxSeenBuild = row.MaxSeenBuild,
        FirstGatedBuild = AppVersionRules.FirstGatedBuild,
        UpdatedByAdminId = row.UpdatedByAdminId,
        UpdatedByAdminName = updatedBy == null ? null : (string.IsNullOrWhiteSpace(updatedBy.Name) ? updatedBy.Email : updatedBy.Name),
        UpdatedAt = row.UpdatedAt,
        Reason = row.Reason,
    };

    public static async Task<IResult> GetUsers(
        ApplicationDbContext db,
        IRoleChangeGuardService guard,
        int page = 1,
        int pageSize = 20,
        bool? isActive = null,
        string? search = null,
        Guid? districtId = null,
        Guid? cityId = null)
    {
        pageSize = Math.Clamp(pageSize, 1, 100);
        page = Math.Max(1, page);

        var query = db.Users.AsNoTracking().AsQueryable();

        if (isActive.HasValue)
            query = query.Where(u => u.IsActive == isActive.Value);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            query = query.Where(u =>
                u.PhoneNumber.Contains(s) ||
                (u.Name != null && u.Name.ToLower().Contains(s)));
        }

        if (districtId.HasValue)
            query = query.Where(u => db.RoomListings.Any(l => l.UserId == u.Id && l.DistrictId == districtId.Value && !l.IsDeleted));

        if (cityId.HasValue)
            query = query.Where(u => db.RoomListings.Any(l => l.UserId == u.Id && l.CityId == cityId.Value && !l.IsDeleted));

        var now = DateTime.UtcNow;
        var projected = query.Select(u => new
        {
            User = u,
            RoleKey = u.Role.Key,
            RoleName = u.Role.Name,
            TotalListings = db.RoomListings.Count(l => l.UserId == u.Id && !l.IsDeleted),
            ActiveListings = db.RoomListings.Count(l => l.UserId == u.Id && !l.IsDeleted && l.IsActive),
            TotalPlotListings = db.PlotListings.Count(l => l.UserId == u.Id && !l.IsDeleted),
            ActivePlotListings = db.PlotListings.Count(l => l.UserId == u.Id && !l.IsDeleted && l.IsActive),
            TotalStays = db.StayListings.Count(s => s.UserId == u.Id && !s.IsDeleted),
            StayStatus = db.StayListings.Where(s => s.UserId == u.Id && !s.IsDeleted).Select(s => s.Status).FirstOrDefault(),
            TotalTaxis = db.TaxiListings.Count(t => t.UserId == u.Id && !t.IsDeleted),
            TaxiStatus = db.TaxiListings.Where(t => t.UserId == u.Id && !t.IsDeleted).Select(t => t.Status).FirstOrDefault(),
            Plan = db.UserPlans
                .Where(p => p.UserId == u.Id && p.Status == UserPlanStatuses.Active && p.EndsAt > now)
                .OrderByDescending(p => p.EndsAt)
                .Select(p => new AdminUserPlanSummaryDto { FeatureKey = p.FeatureKey, PlanName = p.Plan.Name, EndsAt = p.EndsAt, Source = p.Source })
                .FirstOrDefault(),
        });

        var result = await projected
            .OrderByDescending(x => x.User.CreatedAt)
            .ToPagedResultAsync(page, pageSize, x =>
            {
                var u = x.User;
                return new AdminUserDto
                {
                    Id = u.Id,
                    PhoneNumber = u.PhoneNumber,
                    IsPhoneVerified = u.IsPhoneVerified,
                    Name = u.Name,
                    IsActive = u.IsActive,
                    CreatedAt = u.CreatedAt,
                    TotalListings = x.TotalListings,
                    ActiveListings = x.ActiveListings,
                    TotalPlotListings = x.TotalPlotListings,
                    ActivePlotListings = x.ActivePlotListings,
                    TotalStays = x.TotalStays,
                    StayStatus = x.StayStatus,
                    TotalTaxis = x.TotalTaxis,
                    TaxiStatus = x.TaxiStatus,
                    Plan = x.Plan,
                    Role = x.RoleKey,
                    RoleName = x.RoleName,
                };
            });

        var blocking = await guard.GetBlockingCountsAsync(result.Items.Select(u => u.Id).ToList());
        foreach (var item in result.Items)
        {
            item.LiveRoomListings = blocking[item.Id].Rooms;
            item.LivePlotListings = blocking[item.Id].Plots;
            item.LiveStayListings = blocking[item.Id].Stays;
            item.LiveTaxiListings = blocking[item.Id].Taxis;
        }

        return OkResponse(result);
    }

    public static async Task<IResult> GetDeletedAccounts(
        ApplicationDbContext db, int page = 1, int pageSize = 20, string? search = null)
    {
        pageSize = Math.Clamp(pageSize, 1, 100);
        page = Math.Max(1, page);

        var query = db.DeletedAccountRecords.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            query = query.Where(r =>
                r.PhoneNumber.Contains(s) ||
                (r.Name != null && r.Name.ToLower().Contains(s)));
        }

        var result = await query
            .OrderByDescending(r => r.DeletedAt)
            .ToPagedResultAsync(page, pageSize, r => r.Adapt<DeletedAccountRecordDto>());

        return OkResponse(new { items = result.Items, hasMore = result.HasMore });
    }

    public static async Task<IResult> GetUserById(Guid id, ApplicationDbContext db, IRoleChangeGuardService guard)
    {
        var u = await db.Users.AsNoTracking().Include(u => u.Role).FirstOrDefaultAsync(u => u.Id == id);
        if (u == null) return NotFoundResponse("User not found");

        var blocking = (await guard.GetBlockingCountsAsync([id]))[id];

        var dto = new AdminUserDto
        {
            Id = u.Id,
            PhoneNumber = u.PhoneNumber,
            IsPhoneVerified = u.IsPhoneVerified,
            Name = u.Name,
            IsActive = u.IsActive,
            CreatedAt = u.CreatedAt,
            TotalListings = await db.RoomListings.CountAsync(l => l.UserId == id && !l.IsDeleted),
            ActiveListings = await db.RoomListings.CountAsync(l => l.UserId == id && !l.IsDeleted && l.IsActive),
            TotalPlotListings = await db.PlotListings.CountAsync(l => l.UserId == id && !l.IsDeleted),
            ActivePlotListings = await db.PlotListings.CountAsync(l => l.UserId == id && !l.IsDeleted && l.IsActive),
            LiveRoomListings = blocking.Rooms,
            LivePlotListings = blocking.Plots,
            TotalStays = await db.StayListings.CountAsync(s => s.UserId == id && !s.IsDeleted),
            LiveStayListings = blocking.Stays,
            StayStatus = await db.StayListings.Where(s => s.UserId == id && !s.IsDeleted).Select(s => s.Status).FirstOrDefaultAsync(),
            TotalTaxis = await db.TaxiListings.CountAsync(t => t.UserId == id && !t.IsDeleted),
            LiveTaxiListings = blocking.Taxis,
            TaxiStatus = await db.TaxiListings.Where(t => t.UserId == id && !t.IsDeleted).Select(t => t.Status).FirstOrDefaultAsync(),
            Plan = await db.UserPlans.AsNoTracking()
                .Where(p => p.UserId == id && p.Status == UserPlanStatuses.Active && p.EndsAt > DateTime.UtcNow)
                .OrderByDescending(p => p.EndsAt)
                .Select(p => new AdminUserPlanSummaryDto { FeatureKey = p.FeatureKey, PlanName = p.Plan.Name, EndsAt = p.EndsAt, Source = p.Source })
                .FirstOrDefaultAsync(),
            Role = u.Role.Key,
            RoleName = u.Role.Name,
        };

        return OkResponse(dto);
    }

    // Suspend: live rooms/plots become PauseReason=Suspended. Unsuspend revives Suspended rows, newest first,
    // up to the owner's free slots (under the owner lock); the rest become plan-paused, and a slot still free
    // resumes plan-paused rows.
    public static async Task<IResult> UpdateUserStatus(
        Guid id,
        UpdateUserStatusRequest request,
        ApplicationDbContext db,
        ILiveSlotService slots,
        IListingCacheInvalidator cacheInvalidator,
        IServiceProvider sp)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id);
        if (user == null) return NotFoundResponse("User not found");

        var now = DateTime.UtcNow;
        List<Guid?> stayDistrictIds, taxiDistrictIds;
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            await slots.LockOwnerAsync(id);
            user.IsActive = request.IsActive;
            user.UpdatedAt = now;
            await db.SaveChangesAsync();

            if (request.IsActive) await slots.UnsuspendAsync(id);
            else await slots.SuspendAsync(id);

            // Stays and taxis have no paid window; an approved one simply follows the owner.
            var stayQuery = db.StayListings.Where(s => s.UserId == id && !s.IsDeleted && s.Status == StayStatuses.Approved);
            stayDistrictIds = await stayQuery.Select(s => s.DistrictId).Distinct().ToListAsync();
            await stayQuery.ExecuteUpdateAsync(s => s
                .SetProperty(x => x.AcceptingGuests, request.IsActive)
                .SetProperty(x => x.UpdatedAt, now));

            var taxiQuery = db.TaxiListings.Where(t => t.UserId == id && !t.IsDeleted && t.Status == TaxiStatuses.Approved);
            taxiDistrictIds = await taxiQuery.Select(t => t.DistrictId).Distinct().ToListAsync();
            await taxiQuery.ExecuteUpdateAsync(s => s
                .SetProperty(x => x.IsAvailable, request.IsActive)
                .SetProperty(x => x.UpdatedAt, now));

            // Revoke live sessions on suspend so the next request fails OnTokenValidated with 401;
            // re-login is then blocked by the IsActive check in AuthHandlers.
            if (!request.IsActive)
            {
                await db.Sessions
                    .Where(s => s.UserId == id && s.IsActive)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(x => x.IsActive, false)
                        .SetProperty(x => x.LoggedOutAt, now));
            }

            await tx.CommitAsync();
        }

        await cacheInvalidator.BustForUserAsync(id, ListingKinds.Room);
        await cacheInvalidator.BustForUserAsync(id, ListingKinds.Plot);
        var redis = sp.GetService<IConnectionMultiplexer>();
        foreach (var districtId in stayDistrictIds)
            await StayCache.InvalidateAsync(redis, districtId);
        foreach (var districtId in taxiDistrictIds)
            await TaxiCache.InvalidateAsync(redis, districtId);

        return OkResponse(new { id = user.Id, isActive = user.IsActive });
    }

    // ── Admin RoomListing endpoints ───────────────────────────────────────────────

    public record AdminToggleListingRequest(bool IsActive);

    public static async Task<IResult> GetAdminListings(
        ApplicationDbContext db,
        int page = 1,
        int pageSize = 20,
        Guid? districtId = null,
        Guid? cityId = null,
        Guid? roomTypeId = null,
        bool? isActive = null,
        string? search = null)
    {
        pageSize = Math.Clamp(pageSize, 1, 100);
        page = Math.Max(1, page);

        var query = db.RoomListings
            .AsNoTracking()
            .Include(l => l.District)
            .Include(l => l.City)
            .Include(l => l.RoomType)
            .Include(l => l.User)
            .Include(l => l.Photos.OrderBy(p => p.PhotoOrder).Take(1))
            .Where(l => !l.IsDeleted);

        if (districtId.HasValue) query = query.Where(l => l.DistrictId == districtId.Value);
        if (cityId.HasValue) query = query.Where(l => l.CityId == cityId.Value);
        if (roomTypeId.HasValue) query = query.Where(l => l.RoomTypeId == roomTypeId.Value);
        if (isActive.HasValue) query = query.Where(l => l.IsActive == isActive.Value);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            query = query.Where(l =>
                l.User.PhoneNumber.Contains(s) ||
                (l.User.Name != null && l.User.Name.ToLower().Contains(s)));
        }

        var result = await query
            .OrderByDescending(l => l.CreatedAt)
            .ToPagedResultAsync(page, pageSize, l => new AdminListingDto
            {
                Id = l.Id,
                UserId = l.UserId,
                OwnerName = l.User?.Name,
                OwnerPhone = l.User?.PhoneNumber,
                DistrictName = l.District?.Name,
                CityName = l.City?.Name,
                RoomTypeName = l.RoomType?.Name,
                PriceMonthly = l.PriceMonthly,
                IsActive = l.IsActive,
                ValidUntil = l.ValidUntil,
                IsAc = l.IsAc,
                PauseReason = l.PauseReason,
                PriorityRank = l.PriorityRank,
                IsFeatured = l.IsFeatured,
                Address = l.Address,
                ThumbnailUrl = l.Photos.FirstOrDefault()?.PhotoUrl,
                PhotoCount = l.Photos.Count,
                CreatedAt = l.CreatedAt,
            });

        return OkResponse(result);
    }

    public static async Task<IResult> GetAdminListingById(Guid id, IUnitOfWork unitOfWork)
    {
        var listing = await unitOfWork.RoomListings.GetByIdWithPhotosForAdminAsync(id);
        if (listing == null) return NotFoundResponse("RoomListing not found");
        return OkResponse(listing.Adapt<RoomListingDto>());
    }

    // Deactivate = admin pause (owner cannot self-resume). Reactivate needs a valid ValidUntil and a free
    // slot in the owner's plan, checked under the owner lock.
    public static async Task<IResult> ToggleAdminListingStatus(
        Guid id, AdminToggleListingRequest request, ApplicationDbContext db,
        ILiveSlotService slots, IPlanEntitlementService entitlements, IListingCacheInvalidator cacheInvalidator)
    {
        var listing = await db.RoomListings.FindAsync(id);
        if (listing == null || listing.IsDeleted) return NotFoundResponse("RoomListing not found");

        var resumed = 0;
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            await slots.LockOwnerAsync(listing.UserId);
            var freesSlot = !request.IsActive && listing.IsActive && listing.LiveRequestStatus != GoLiveRequestStatuses.Pending;
            if (request.IsActive)
            {
                if (!listing.IsActive)
                {
                    if (!(listing.ValidUntil > DateTime.UtcNow))
                        return BadRequestResponse("This listing has no valid live period. Ask the owner to Go Live.");
                    var limits = await entitlements.GetLimitsAsync(listing.UserId, ListingKinds.Room);
                    var live = await slots.CountLiveAsync(listing.UserId, ListingKinds.Room, listing.Id);
                    if (live >= limits.ListingLimit)
                        return GoLiveHandlers.PlanLimitReached(limits.ListingLimit, live);
                    listing.IsActive = true;
                    listing.PauseReason = null;
                    listing.PriorityRank = limits.PriorityRank;
                    listing.IsFeatured = limits.IsFeatured;
                }
            }
            else if (listing.IsActive || listing.PauseReason != PauseReasons.Admin)
            {
                listing.IsActive = false;
                listing.PauseReason = PauseReasons.Admin;
            }
            listing.UpdatedAt = DateTime.UtcNow;
            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                return ConflictResponse("This listing was just modified by another request. Please retry.", "CONCURRENT_UPDATE");
            }
            if (freesSlot)
                resumed = await slots.ResumeAfterSlotFreedAsync(listing.UserId, ListingKinds.Room);
            await tx.CommitAsync();
        }

        await cacheInvalidator.BustDistrictsAsync(ListingKinds.Room, [listing.DistrictId]);
        if (resumed > 0)
            await cacheInvalidator.BustForUserAsync(listing.UserId, ListingKinds.Room);

        return OkResponse(new { success = true, isActive = listing.IsActive, pauseReason = listing.PauseReason });
    }

    private static async Task InvalidateRecentRoomsCacheAsync(IConnectionMultiplexer? redis)
    {
        if (redis == null) return;
        try { await redis.GetDatabase().KeyDeleteAsync("home:recentRooms"); } catch { }
    }

    private static async Task InvalidateForYouRoomsCacheAsync(IConnectionMultiplexer? redis, Guid districtId)
    {
        if (redis == null) return;
        try { await redis.GetDatabase().KeyDeleteAsync($"home:forYouRooms:{districtId}"); } catch { }
    }

    // Missing here before this feature — its Plot sibling (PlotListingHandlers.AdminDeletePlotListing)
    // already busts this cache on delete; Room's admin delete didn't.
    private static async Task InvalidateNearbyCacheAsync(IConnectionMultiplexer? redis, Guid districtId)
    {
        if (redis == null) return;
        try
        {
            var db = redis.GetDatabase();
            var server = redis.GetServers().FirstOrDefault(s => s.IsConnected);
            if (server == null) return;
            await foreach (var key in server.KeysAsync(pattern: $"nearby:{districtId}:*"))
                await db.KeyDeleteAsync(key);
        }
        catch { }
    }

    public static async Task<IResult> DeleteAdminListing(
        Guid id, IUnitOfWork unitOfWork, IPhotoService photoService, ILiveSlotService slots, IServiceProvider sp)
    {
        // Tracked fetch: the shadow xmin concurrency token only round-trips through a tracked entity.
        var listing = await unitOfWork.RoomListings.GetByIdAsync(id);
        if (listing == null || listing.IsDeleted) return NotFoundResponse("RoomListing not found");

        await photoService.DeleteRoomPhotosAsync(listing.UserId, id);

        var resumed = 0;
        await unitOfWork.BeginTransactionAsync();
        try
        {
            await slots.LockOwnerAsync(listing.UserId);
            var wasLive = listing.IsActive || listing.LiveRequestStatus == GoLiveRequestStatuses.Pending;
            listing.IsDeleted = true;
            listing.DeletedAt = DateTime.UtcNow;
            await unitOfWork.RoomListings.UpdateAsync(listing);
            try
            {
                await unitOfWork.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                await unitOfWork.RollbackTransactionAsync();
                return ConflictResponse("This listing was just modified by another request. Please retry.", "CONCURRENT_UPDATE");
            }
            if (wasLive)
                resumed = await slots.ResumeAfterSlotFreedAsync(listing.UserId, ListingKinds.Room);
            await unitOfWork.CommitTransactionAsync();
        }
        catch
        {
            await unitOfWork.RollbackTransactionAsync();
            throw;
        }

        var redis = sp.GetService<IConnectionMultiplexer>();
        await InvalidateNearbyCacheAsync(redis, listing.DistrictId);
        await InvalidateRecentRoomsCacheAsync(redis);
        await InvalidateForYouRoomsCacheAsync(redis, listing.DistrictId);
        if (resumed > 0)
            await sp.GetRequiredService<IListingCacheInvalidator>().BustForUserAsync(listing.UserId, ListingKinds.Room);

        return NoContentResponse();
    }

    private static AdminListingReportDto ToAdminListingReportDto(ListingReport r) => new()
    {
        Id = r.Id,
        ListingId = r.ListingId,
        ListingType = r.ListingType,
        ReporterUserId = r.ReporterUserId,
        ReporterName = r.ReporterName,
        ReporterMobile = r.ReporterMobile,
        ReportedUserId = r.ReportedUserId,
        ReportedName = r.ReportedName,
        ReportedMobile = r.ReportedMobile,
        ReasonName = r.Reason?.Name ?? "",
        Details = r.Details,
        Status = r.Status,
        ResolutionAction = r.ResolutionAction,
        CreatedAt = r.CreatedAt,
        ResolvedAt = r.ResolvedAt,
    };

    private static void ApplyRoomReportDetails(AdminListingReportDto dto, RoomListing l)
    {
        dto.Address = l.Address;
        dto.Description = l.Description;
        dto.DistrictName = l.District?.Name;
        dto.CityName = l.City?.Name;
        dto.RoomTypeName = l.RoomType?.Name;
        dto.FurnishedStatus = l.FurnishedStatus;
        dto.IsAc = l.IsAc;
        dto.SecurityDeposit = l.SecurityDeposit;
        dto.Floor = l.Floor;
        dto.PriceMonthly = l.PriceMonthly;
    }

    private static void ApplyPlotReportDetails(AdminListingReportDto dto, PlotListing p)
    {
        dto.Address = p.Address;
        dto.Description = p.Description;
        dto.DistrictName = p.District?.Name;
        dto.CityName = p.City?.Name;
        dto.PlotType = p.PlotType?.Name;
        dto.AreaValue = p.AreaValue;
        dto.AreaUnit = p.AreaUnit;
        dto.AreaSqft = p.AreaSqft;
    }

    public static async Task<IResult> GetReports(
        IUnitOfWork unitOfWork, ApplicationDbContext db, int page = 1, int pageSize = 20, string? status = "Pending")
    {
        pageSize = Math.Clamp(pageSize, 1, 100);
        page = Math.Max(1, page);

        var paged = await unitOfWork.ListingReports.GetPagedAsync(page, pageSize, status);

        // Batched lookup instead of N+1 — ListingReport has no FK/nav to RoomListing/PlotListing
        // (just a ListingId + ListingType string discriminator), so the underlying listing content
        // has to be joined in manually here.
        var roomIds = paged.Items.Where(r => r.ListingType == ListingKinds.Room).Select(r => r.ListingId).Distinct().ToList();
        var plotIds = paged.Items.Where(r => r.ListingType == ListingKinds.Plot).Select(r => r.ListingId).Distinct().ToList();
        var stayIds = paged.Items.Where(r => r.ListingType == ListingKinds.Stay).Select(r => r.ListingId).Distinct().ToList();
        var taxiIds = paged.Items.Where(r => r.ListingType == ListingKinds.Taxi).Select(r => r.ListingId).Distinct().ToList();

        var roomsById = roomIds.Count == 0 ? new Dictionary<Guid, RoomListing>() : await db.RoomListings
            .AsNoTracking().Include(l => l.RoomType).Include(l => l.District).Include(l => l.City)
            .Where(l => roomIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id);
        var plotsById = plotIds.Count == 0 ? new Dictionary<Guid, PlotListing>() : await db.PlotListings
            .AsNoTracking().Include(p => p.PlotType).Include(p => p.District).Include(p => p.City)
            .Where(p => plotIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id);
        var staysById = stayIds.Count == 0 ? new Dictionary<Guid, StayListing>() : await db.StayListings
            .AsNoTracking().Include(s => s.StayType).Include(s => s.District).Include(s => s.City)
            .Where(s => stayIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id);
        var taxisById = taxiIds.Count == 0 ? new Dictionary<Guid, TaxiListing>() : await db.TaxiListings
            .AsNoTracking().Include(t => t.VehicleType).Include(t => t.District).Include(t => t.City)
            .Where(t => taxiIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id);

        var items = paged.Items.Select(r =>
        {
            var dto = ToAdminListingReportDto(r);
            if (r.ListingType == ListingKinds.Room && roomsById.TryGetValue(r.ListingId, out var room)) ApplyRoomReportDetails(dto, room);
            else if (r.ListingType == ListingKinds.Plot && plotsById.TryGetValue(r.ListingId, out var plot)) ApplyPlotReportDetails(dto, plot);
            else if (r.ListingType == ListingKinds.Stay && staysById.TryGetValue(r.ListingId, out var stay)) AdminStayHandlers.ApplyStayReportDetails(dto, stay);
            else if (r.ListingType == ListingKinds.Taxi && taxisById.TryGetValue(r.ListingId, out var taxi)) AdminTaxiHandlers.ApplyTaxiReportDetails(dto, taxi);
            return dto;
        }).ToList();

        return OkResponse(new { items, hasMore = paged.HasMore });
    }

    public static async Task<IResult> GetReportById(Guid id, IUnitOfWork unitOfWork)
    {
        var report = await unitOfWork.ListingReports.GetByIdAsync(id);
        if (report == null) return NotFoundResponse("Report not found");
        var dto = ToAdminListingReportDto(report);

        // GetByIdWithPhotosForAdminAsync deliberately survives the listing being deleted, so the
        // report review still shows what was reported even after the owner deletes the post.
        if (report.ListingType == ListingKinds.Room)
        {
            var listing = await unitOfWork.RoomListings.GetByIdWithPhotosForAdminAsync(report.ListingId);
            if (listing != null)
            {
                ApplyRoomReportDetails(dto, listing);
                dto.Photos = listing.Photos.Select(p => p.PhotoUrl).ToList();
            }
        }
        else if (report.ListingType == ListingKinds.Plot)
        {
            var plot = await unitOfWork.PlotListings.GetByIdWithPhotosForAdminAsync(report.ListingId);
            if (plot != null)
            {
                ApplyPlotReportDetails(dto, plot);
                dto.Photos = plot.Photos.Select(p => p.PhotoUrl).ToList();
            }
        }
        else if (report.ListingType == ListingKinds.Stay)
        {
            var stay = await unitOfWork.Stays.GetByIdForAdminAsync(report.ListingId);
            if (stay != null)
            {
                AdminStayHandlers.ApplyStayReportDetails(dto, stay);
                dto.Photos = stay.Photos.OrderBy(p => p.PhotoOrder).Select(p => p.PhotoUrl).ToList();
            }
        }
        else if (report.ListingType == ListingKinds.Taxi)
        {
            var taxi = await unitOfWork.Taxis.GetByIdForAdminAsync(report.ListingId);
            if (taxi != null)
            {
                AdminTaxiHandlers.ApplyTaxiReportDetails(dto, taxi);
                if (taxi.PlatePhotoUrl != null) dto.Photos = [taxi.PlatePhotoUrl];
            }
        }

        return OkResponse(dto);
    }

    public static async Task<IResult> ResolveReport(
        Guid id, ResolveReportRequest request, IValidator<ResolveReportRequest> validator,
        ClaimsPrincipal principal, IUnitOfWork unitOfWork)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var report = await unitOfWork.ListingReports.GetByIdAsync(id);
        if (report == null) return NotFoundResponse("Report not found");
        if (report.Status != "Pending") return BadRequestResponse("Report has already been resolved");

        var adminIdClaim = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        Guid? adminId = Guid.TryParse(adminIdClaim, out var parsedAdminId) ? parsedAdminId : null;

        await unitOfWork.ListingReports.ResolveAsync(id, adminId, request.ResolutionAction);

        return OkResponse(new { success = true });
    }

    // ── Report-driven takedown ("Reject") ──────────────────────────────────────
    // Replaces the old two-call "Deactivate Post" (PUT status + PUT resolve, non-transactional) with
    // one atomic endpoint that reuses the Go-Live moderation Rejected mechanism from a second entry
    // point: a report resolution. It targets a listing that was already live and mirrors
    // RejectRoomGoLiveRequest/RejectPlotGoLiveRequest (transaction shape,
    // DbUpdateConcurrencyException -> CONCURRENT_UPDATE, GoLiveRequestStatuses.Rejected/RejectedReason).
    // One HTTP route (report.ListingType is already a plain string spanning both kinds), but the
    // Room/Plot code paths stay separately duplicated per this codebase's convention: Room lives
    // here (RejectReportedRoomAsync, mirroring where RejectRoomGoLiveRequest lives), Plot lives in
    // PlotListingHandlers.RejectReportedPlotAsync (mirroring RejectPlotGoLiveRequest) so it can reuse
    // that file's own Plot-specific cache-key helpers instead of duplicating those key strings here.

    private static NotificationEvent BuildRoomReportRejectedNotification(Guid userId, string reason) => new()
    {
        Id = Guid.NewGuid(),
        TargetUserId = userId,
        Type = NotificationTypes.GoLiveRejected,
        Title = "Your listing was taken down",
        Body = $"Your Room listing was taken down: {reason}",
        ActionRoute = "/my-listings",
        CreatedAt = DateTime.UtcNow,
    };

    public static async Task<IResult> RejectReport(
        Guid id, RejectGoLiveRequest request, IValidator<RejectGoLiveRequest> validator,
        ClaimsPrincipal principal, IUnitOfWork unitOfWork, ApplicationDbContext db, IServiceProvider sp,
        IHubContext<NotificationHub> hubContext, IRabbitMqPublisher publisher, ILiveSlotService slots)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var report = await unitOfWork.ListingReports.GetByIdAsync(id);
        if (report == null) return NotFoundResponse("Report not found");
        if (report.Status != "Pending") return BadRequestResponse("Report has already been resolved");

        var adminIdClaim = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        Guid? adminId = Guid.TryParse(adminIdClaim, out var parsedAdminId) ? parsedAdminId : null;

        return report.ListingType switch
        {
            ListingKinds.Room => await RejectReportedRoomAsync(report, request.Reason, adminId, unitOfWork, db, sp, hubContext, publisher, slots),
            ListingKinds.Plot => await PlotListingHandlers.RejectReportedPlotAsync(report, request.Reason, adminId, unitOfWork, db, sp, hubContext, publisher, slots),
            ListingKinds.Stay => await AdminStayHandlers.RejectReportedStayAsync(report, request.Reason, adminId, unitOfWork, db, sp, hubContext, publisher),
            ListingKinds.Taxi => await AdminTaxiHandlers.RejectReportedTaxiAsync(report, request.Reason, adminId, unitOfWork, db, sp, hubContext, publisher),
            _ => BadRequestResponse($"Unknown listing type '{report.ListingType}' on this report"),
        };
    }

    private static async Task<IResult> RejectReportedRoomAsync(
        ListingReport report, string reason, Guid? adminId,
        IUnitOfWork unitOfWork, ApplicationDbContext db, IServiceProvider sp,
        IHubContext<NotificationHub> hubContext, IRabbitMqPublisher publisher, ILiveSlotService slots)
    {
        var listing = await unitOfWork.RoomListings.GetByIdAsync(report.ListingId);
        if (listing == null || listing.IsDeleted) return NotFoundResponse("RoomListing not found");

        // Rejected rows never count against the owner's plan limit; a freed slot resumes a plan-paused row.
        var resumed = 0;
        await unitOfWork.BeginTransactionAsync();
        try
        {
            await slots.LockOwnerAsync(listing.UserId);
            var wasLive = listing.IsActive || listing.LiveRequestStatus == GoLiveRequestStatuses.Pending;
            listing.IsActive = false;
            listing.PauseReason = null;
            listing.LiveRequestStatus = GoLiveRequestStatuses.Rejected;
            listing.RejectedReason = reason;
            // This listing was genuinely live, so ValidUntil is still some future paid-through date —
            // left alone, GoLiveHandlers' free-reactivation branch (which only ever checks
            // IsActive/ValidUntil, not LiveRequestStatus) would let the owner silently reactivate it
            // for free before that date passes, undoing this rejection entirely. Expiring it here
            // fixes the data at its source instead of special-casing Rejected inside that shared,
            // heavily-used branch.
            listing.ValidUntil = DateTime.UtcNow.AddDays(-1);
            listing.UpdatedAt = DateTime.UtcNow;
            await unitOfWork.RoomListings.UpdateAsync(listing);

            var notification = BuildRoomReportRejectedNotification(listing.UserId, reason);
            db.NotificationEvents.Add(notification);

            try
            {
                await unitOfWork.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                await unitOfWork.RollbackTransactionAsync();
                return ConflictResponse("This listing was just modified by another request. Please retry.", "CONCURRENT_UPDATE");
            }

            await unitOfWork.ListingReports.ResolveAsync(report.Id, adminId, "PostRejected");

            if (wasLive)
                resumed = await slots.ResumeAfterSlotFreedAsync(listing.UserId, ListingKinds.Room);

            await unitOfWork.CommitTransactionAsync();

            var redis = sp.GetService<IConnectionMultiplexer>();
            await InvalidateNearbyCacheAsync(redis, listing.DistrictId);
            await InvalidateRecentRoomsCacheAsync(redis);
            await InvalidateForYouRoomsCacheAsync(redis, listing.DistrictId);
            if (resumed > 0)
                await sp.GetRequiredService<IListingCacheInvalidator>().BustForUserAsync(listing.UserId, ListingKinds.Room);

            await SendGoLiveNotificationAsync(hubContext, publisher, notification);

            return OkResponse(new { success = true });
        }
        catch
        {
            await unitOfWork.RollbackTransactionAsync();
            throw;
        }
    }

    // ── Room Go-Live moderation ──────────────────────────────────────────────
    // Mirrors GetReports/GetReportById/ResolveReport's shape exactly. Separate from Plot's own pair
    // in PlotListingHandlers — these fields live directly on RoomListing/PlotListing, so a unified
    // feed would need a cross-entity UNION with correct SQL-level pagination for no real benefit,
    // and breaks from this codebase's dominant Room/Plot-duplication convention.

    private static GoLiveRequestDto ToGoLiveRequestDto(RoomListing l, bool includePhotos) => new()
    {
        Id = l.Id,
        UserId = l.UserId,
        OwnerName = l.User?.Name,
        OwnerMobile = l.User?.PhoneNumber,
        Status = l.LiveRequestStatus ?? string.Empty,
        RequestedPlanDays = l.RequestedPlanDays,
        SubmittedAt = l.UpdatedAt,
        Photos = includePhotos ? l.Photos.Select(p => p.PhotoUrl).ToList() : new List<string>(),
        Address = l.Address,
        Description = l.Description,
        DistrictName = l.District?.Name,
        CityName = l.City?.Name,
        RoomTypeName = l.RoomType?.Name,
        FurnishedStatus = l.FurnishedStatus,
        IsAc = l.IsAc,
        SecurityDeposit = l.SecurityDeposit,
        Floor = l.Floor,
        PriceMonthly = l.PriceMonthly,
    };

    public static async Task<IResult> GetRoomGoLiveRequests(
        ApplicationDbContext db, IPlanEntitlementService entitlements, int page = 1, int pageSize = 20, string? status = "Pending")
    {
        pageSize = Math.Clamp(pageSize, 1, 100);
        page = Math.Max(1, page);

        // Deleted listings never leave LiveRequestStatus behind — excluded here the same way
        // GetAdminListings excludes them, so a deleted-while-Pending listing can't linger in this
        // queue forever.
        var query = db.RoomListings
            .AsNoTracking()
            .Include(l => l.User)
            .Include(l => l.RoomType)
            .Include(l => l.District)
            .Include(l => l.City)
            .Where(l => !l.IsDeleted && l.LiveRequestStatus == status)
            .OrderByDescending(l => l.UpdatedAt);

        var result = await query.ToPagedResultAsync(page, pageSize, l => ToGoLiveRequestDto(l, includePhotos: false));
        if (result.Items.Count > 0)
        {
            var limits = await entitlements.GetLimitsBatchAsync(result.Items.Select(i => i.UserId).Distinct().ToList(), ListingKinds.Room);
            foreach (var i in result.Items)
                i.OwnerPlanName = limits.TryGetValue(i.UserId, out var l) ? l.PlanName : null;
        }
        return OkResponse(new { items = result.Items, hasMore = result.HasMore });
    }

    public static async Task<IResult> GetRoomGoLiveRequestById(Guid id, ApplicationDbContext db, IPlanEntitlementService entitlements)
    {
        var listing = await db.RoomListings
            .AsNoTracking()
            .Include(l => l.User)
            .Include(l => l.RoomType)
            .Include(l => l.District)
            .Include(l => l.City)
            .Include(l => l.Photos.OrderBy(p => p.PhotoOrder))
            .FirstOrDefaultAsync(l => l.Id == id && !l.IsDeleted);
        if (listing == null) return NotFoundResponse("Go-Live request not found");
        var dto = ToGoLiveRequestDto(listing, includePhotos: true);
        dto.OwnerPlanName = (await entitlements.GetLimitsAsync(listing.UserId, ListingKinds.Room)).PlanName;
        return OkResponse(dto);
    }

    private static NotificationEvent BuildRoomGoLiveApprovedNotification(Guid userId) => new()
    {
        Id = Guid.NewGuid(),
        TargetUserId = userId,
        Type = NotificationTypes.GoLiveApproved,
        Title = "Your listing is now live",
        Body = "Your Room listing has been approved and is now live.",
        ActionRoute = "/my-listings",
        CreatedAt = DateTime.UtcNow,
    };

    private static NotificationEvent BuildRoomGoLiveRejectedNotification(Guid userId, string reason) => new()
    {
        Id = Guid.NewGuid(),
        TargetUserId = userId,
        Type = NotificationTypes.GoLiveRejected,
        Title = "Your listing was not approved",
        Body = $"Your Room listing was rejected. Reason: {reason}",
        ActionRoute = "/my-listings",
        CreatedAt = DateTime.UtcNow,
    };

    // Single-notification push shape — SignalR
    // user_{id} push via NotificationHub (the same hub every other generic NotificationEvent producer
    // reuses) + RabbitMQ notification.push publish for NotificationPushWorkerService. Best-effort:
    // a push failure must never turn an already-committed Approve/Reject into an error response.
    internal static async Task SendGoLiveNotificationAsync(
        IHubContext<NotificationHub> hubContext, IRabbitMqPublisher publisher, NotificationEvent notification)
    {
        try
        {
            await hubContext.Clients.Group($"user_{notification.TargetUserId}").SendAsync("NotificationReceived", new
            {
                id = notification.Id,
                type = notification.Type,
                title = notification.Title,
                body = notification.Body,
                actionRoute = notification.ActionRoute,
            });
        }
        catch { }

        try
        {
            var payload = JsonSerializer.Serialize(new NotificationPushPayload(notification.Id, notification.TargetUserId!.Value));
            await publisher.PublishAsync("notification.push", payload);
        }
        catch { }
    }

    private static NotificationEvent BuildRoomApprovedPausedNotification(Guid userId) => new()
    {
        Id = Guid.NewGuid(),
        TargetUserId = userId,
        Type = NotificationTypes.GoLiveApproved,
        Title = "Your listing is approved",
        Body = "Your Room listing is approved. Your plan limit is full, so it is paused. Swap or upgrade to make it live.",
        ActionRoute = "/my-listings",
        CreatedAt = DateTime.UtcNow,
    };

    private static NotificationEvent BuildRoomApprovedSuspendedNotification(Guid userId) => new()
    {
        Id = Guid.NewGuid(),
        TargetUserId = userId,
        Type = NotificationTypes.GoLiveApproved,
        Title = "Your listing is approved",
        Body = "Your Room listing is approved. It goes live when your account is active again.",
        ActionRoute = "/my-listings",
        CreatedAt = DateTime.UtcNow,
    };

    // Within the owner's plan limit -> live till the resolved ValidUntil; over it -> Approved but plan-paused.
    public static async Task<IResult> ApproveRoomGoLiveRequest(
        Guid id, IUnitOfWork unitOfWork, ApplicationDbContext db, IServiceProvider sp,
        ILiveSlotService slots, IPlanEntitlementService entitlements,
        IHubContext<NotificationHub> hubContext, IRabbitMqPublisher publisher)
    {
        var listing = await unitOfWork.RoomListings.GetByIdAsync(id);
        if (listing == null || listing.IsDeleted) return NotFoundResponse("RoomListing not found");
        if (listing.LiveRequestStatus != GoLiveRequestStatuses.Pending)
            return BadRequestResponse("This request has already been resolved");

        await unitOfWork.BeginTransactionAsync();
        try
        {
            await slots.LockOwnerAsync(listing.UserId);
            var limits = await entitlements.GetLimitsAsync(listing.UserId, ListingKinds.Room);
            var live = await slots.CountLiveAsync(listing.UserId, ListingKinds.Room, listing.Id);
            // A suspended owner's row is approved but held as Suspended, so UnsuspendAsync revives it within the slots.
            var ownerActive = await db.Users.AsNoTracking()
                .Where(u => u.Id == listing.UserId).Select(u => u.IsActive).FirstOrDefaultAsync();
            var goesLive = ownerActive && live < limits.ListingLimit;

            listing.LiveRequestStatus = GoLiveRequestStatuses.Approved;
            listing.IsActive = goesLive;
            listing.PauseReason = goesLive ? null : ownerActive ? PauseReasons.Plan : PauseReasons.Suspended;
            listing.ValidUntil = goesLive ? entitlements.ResolveValidUntil(limits) : listing.ValidUntil;
            listing.PriorityRank = limits.PriorityRank;
            listing.IsFeatured = limits.IsFeatured;
            listing.UpdatedAt = DateTime.UtcNow;
            await unitOfWork.RoomListings.UpdateAsync(listing);

            var notification = goesLive
                ? BuildRoomGoLiveApprovedNotification(listing.UserId)
                : ownerActive
                    ? BuildRoomApprovedPausedNotification(listing.UserId)
                    : BuildRoomApprovedSuspendedNotification(listing.UserId);
            db.NotificationEvents.Add(notification);

            try
            {
                await unitOfWork.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                await unitOfWork.RollbackTransactionAsync();
                return ConflictResponse("This listing was just modified by another request. Please retry.", "CONCURRENT_UPDATE");
            }

            await unitOfWork.CommitTransactionAsync();

            if (goesLive)
            {
                var redis = sp.GetService<IConnectionMultiplexer>();
                await InvalidateNearbyCacheAsync(redis, listing.DistrictId);
                await InvalidateRecentRoomsCacheAsync(redis);
                await InvalidateForYouRoomsCacheAsync(redis, listing.DistrictId);
            }

            await SendGoLiveNotificationAsync(hubContext, publisher, notification);

            return OkResponse(new { success = true, isActive = listing.IsActive, validUntil = listing.ValidUntil, pauseReason = listing.PauseReason });
        }
        catch
        {
            await unitOfWork.RollbackTransactionAsync();
            throw;
        }
    }

    public static async Task<IResult> RejectRoomGoLiveRequest(
        Guid id, RejectGoLiveRequest request, IValidator<RejectGoLiveRequest> validator,
        IUnitOfWork unitOfWork, ApplicationDbContext db,
        IHubContext<NotificationHub> hubContext, IRabbitMqPublisher publisher,
        ILiveSlotService slots, IListingCacheInvalidator cacheInvalidator)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var listing = await unitOfWork.RoomListings.GetByIdAsync(id);
        if (listing == null || listing.IsDeleted) return NotFoundResponse("RoomListing not found");
        if (listing.LiveRequestStatus != GoLiveRequestStatuses.Pending)
            return BadRequestResponse("This request has already been resolved");

        // The Pending row held a slot; the newest plan-paused row takes it in the same transaction.
        var resumed = 0;
        NotificationEvent notification;
        await unitOfWork.BeginTransactionAsync();
        try
        {
            await slots.LockOwnerAsync(listing.UserId);
            listing.LiveRequestStatus = GoLiveRequestStatuses.Rejected;
            listing.RejectedReason = request.Reason;
            listing.PauseReason = null;
            listing.UpdatedAt = DateTime.UtcNow;
            await unitOfWork.RoomListings.UpdateAsync(listing);

            notification = BuildRoomGoLiveRejectedNotification(listing.UserId, request.Reason);
            db.NotificationEvents.Add(notification);

            try
            {
                await unitOfWork.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                await unitOfWork.RollbackTransactionAsync();
                return ConflictResponse("This listing was just modified by another request. Please retry.", "CONCURRENT_UPDATE");
            }

            resumed = await slots.ResumeAfterSlotFreedAsync(listing.UserId, ListingKinds.Room);
            await unitOfWork.CommitTransactionAsync();
        }
        catch
        {
            await unitOfWork.RollbackTransactionAsync();
            throw;
        }

        if (resumed > 0)
            await cacheInvalidator.BustForUserAsync(listing.UserId, ListingKinds.Room);

        await SendGoLiveNotificationAsync(hubContext, publisher, notification);

        return OkResponse(new { success = true });
    }
}
