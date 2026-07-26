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
        var updated = await db.Districts
            .Where(d => d.StateName == stateName)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.IsActive, request.IsActive));

        if (updated == 0)
            return NotFoundResponse($"No districts found for state '{stateName}'");

        cache.Remove("states");
        cache.Remove("districts");
        cache.Remove("cities_all");
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
        await FlushContextCacheAsync(sp.GetService<IConnectionMultiplexer>());
        return OkResponse(new { success = true, isActive = true });
    }

    public static async Task<IResult> DeleteDistrict(Guid id, IUnitOfWork unitOfWork, IMemoryCache cache, ApplicationDbContext db, IServiceProvider sp)
    {
        var district = await unitOfWork.Districts.GetByIdAsync(id);
        if (district == null) return NotFoundResponse("District not found");

        if (await db.RoomListings.AnyAsync(l => l.DistrictId == id && l.IsActive && !l.IsDeleted))
            return BadRequestResponse("Cannot delete district with active listings");

        await unitOfWork.Districts.DeleteAsync(district);
        await unitOfWork.SaveChangesAsync();

        cache.Remove("states");
        cache.Remove("districts");
        cache.Remove($"cities_{id}");
        cache.Remove("cities_all");
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

        // Real-money earnings now come from credit-pack purchases only — Go Live itself spends credits,
        // not rupees, so PaymentTransaction (deleted with the old membership system) is no longer
        // the revenue source.
        var now = DateTime.UtcNow;
        var totalEarnings = await db.CreditPackPurchases
            .Where(p => p.Status == CreditPackPurchaseStatuses.Success)
            .SumAsync(p => (int?)p.PriceInr) ?? 0;
        var currentMonthEarnings = await db.CreditPackPurchases
            .Where(p => p.Status == CreditPackPurchaseStatuses.Success && p.CompletedAt.HasValue &&
                        p.CompletedAt.Value.Year == now.Year &&
                        p.CompletedAt.Value.Month == now.Month)
            .SumAsync(p => (int?)p.PriceInr) ?? 0;

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

    // ── Credit Packs ──────────────────────────────────────────────────────────────
    // Mirrors the Room/Plot Plans pattern below: IsEnabled toggle, no delete, bottom-sheet-sized form.

    public static async Task<IResult> GetCreditPacks(IUnitOfWork unitOfWork)
    {
        var packs = await unitOfWork.CreditPacks.GetAllAsync();
        return OkResponse(packs.OrderBy(p => p.SortOrder).Select(p => new CreditPackDto
        {
            Id = p.Id, Credits = p.Credits, BonusCredits = p.BonusCredits, PriceInr = p.PriceInr,
            IsEnabled = p.IsEnabled, SortOrder = p.SortOrder, IsFeatured = p.IsFeatured,
        }));
    }

    public static async Task<IResult> CreateCreditPack(
        CreateCreditPackRequest request, IValidator<CreateCreditPackRequest> validator,
        IUnitOfWork unitOfWork, IMemoryCache cache)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var pack = new CreditPack
        {
            Id = Guid.NewGuid(), Credits = request.Credits, BonusCredits = request.BonusCredits,
            PriceInr = request.PriceInr, SortOrder = request.SortOrder, IsFeatured = request.IsFeatured,
            IsEnabled = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        await unitOfWork.CreditPacks.AddAsync(pack);
        await unitOfWork.SaveChangesAsync();

        cache.Remove(CreditPackHandlers.ActivePacksCacheKey);

        return CreatedResponse(new CreditPackDto
        {
            Id = pack.Id, Credits = pack.Credits, BonusCredits = pack.BonusCredits, PriceInr = pack.PriceInr,
            IsEnabled = pack.IsEnabled, SortOrder = pack.SortOrder, IsFeatured = pack.IsFeatured,
        }, $"/api/v1/admin/credit-packs/{pack.Id}");
    }

    public static async Task<IResult> UpdateCreditPack(
        Guid id, UpdateCreditPackRequest request, IValidator<UpdateCreditPackRequest> validator,
        IUnitOfWork unitOfWork, IMemoryCache cache)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var pack = await unitOfWork.CreditPacks.GetByIdAsync(id);
        if (pack == null) return NotFoundResponse("Credit pack not found");

        if (request.Credits.HasValue) pack.Credits = request.Credits.Value;
        if (request.BonusCredits.HasValue) pack.BonusCredits = request.BonusCredits.Value;
        if (request.PriceInr.HasValue) pack.PriceInr = request.PriceInr.Value;
        if (request.SortOrder.HasValue) pack.SortOrder = request.SortOrder.Value;
        if (request.IsFeatured.HasValue) pack.IsFeatured = request.IsFeatured.Value;
        if (request.IsEnabled.HasValue) pack.IsEnabled = request.IsEnabled.Value;
        pack.UpdatedAt = DateTime.UtcNow;

        await unitOfWork.CreditPacks.UpdateAsync(pack);
        await unitOfWork.SaveChangesAsync();

        cache.Remove(CreditPackHandlers.ActivePacksCacheKey);

        return OkResponse(new CreditPackDto
        {
            Id = pack.Id, Credits = pack.Credits, BonusCredits = pack.BonusCredits, PriceInr = pack.PriceInr,
            IsEnabled = pack.IsEnabled, SortOrder = pack.SortOrder, IsFeatured = pack.IsFeatured,
        });
    }

    // ── Listing Limits ──────────────────────────────────────────────────────────
    // Two seeded rows (Room/Plot) — a dedicated entity, not a repurposed AppFeature field (see
    // design doc §2a for why that alternative was rejected).

    public static async Task<IResult> GetListingLimits(IUnitOfWork unitOfWork)
    {
        var settings = await unitOfWork.ListingLimitSettings.GetAllAsync();
        return OkResponse(settings.Select(s => new AdminListingLimitDto
        {
            Id = s.Id, ListingKind = s.ListingKind, MaxListings = s.MaxListings, UpdatedAt = s.UpdatedAt,
        }));
    }

    public static async Task<IResult> UpdateListingLimit(
        string kind, UpdateListingLimitSettingRequest request, IValidator<UpdateListingLimitSettingRequest> validator,
        IUnitOfWork unitOfWork, IMemoryCache cache)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var setting = await unitOfWork.ListingLimitSettings.GetByKindAsync(kind);
        if (setting == null) return NotFoundResponse($"No listing limit setting for kind '{kind}'");

        await unitOfWork.ListingLimitSettings.UpdateMaxListingsAsync(kind, request.MaxListings);

        cache.Remove(ConfigHandlers.ListingLimitsCacheKey);

        return OkResponse(new { listingKind = kind, maxListings = request.MaxListings });
    }

    // ── Payment Kill Switch ─────────────────────────────────────────────────────
    // Single global row (RentNearBy.Core.Models.AppFeatureKeys.PaymentEnabled) — a dedicated, generic
    // AppFeatureFlag entity, not a repurposed CreditFeature/ListingLimitSetting row.

    public static async Task<IResult> GetPaymentFeature(IUnitOfWork unitOfWork)
    {
        var flag = await unitOfWork.AppFeatureFlags.GetByKeyAsync(AppFeatureKeys.PaymentEnabled);
        if (flag == null) return NotFoundResponse("Payment feature flag not found");

        return OkResponse(new
        {
            isEnabled = flag.IsEnabled,
            freeDurationDays = flag.FreeDurationDays ?? 30,
            updatedByAdminId = flag.UpdatedByAdminId,
            updatedAt = flag.UpdatedAt,
            reason = flag.Reason,
        });
    }

    public static async Task<IResult> UpdatePaymentFeature(
        UpdatePaymentFeatureRequest request, IValidator<UpdatePaymentFeatureRequest> validator,
        ClaimsPrincipal principal, IUnitOfWork unitOfWork, IMemoryCache cache)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var flag = await unitOfWork.AppFeatureFlags.GetByKeyAsync(AppFeatureKeys.PaymentEnabled);
        if (flag == null) return NotFoundResponse("Payment feature flag not found");

        if (request.IsEnabled.HasValue) flag.IsEnabled = request.IsEnabled.Value;
        if (request.FreeDurationDays.HasValue) flag.FreeDurationDays = request.FreeDurationDays.Value;
        if (request.Reason != null) flag.Reason = request.Reason;

        var adminIdClaim = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        flag.UpdatedByAdminId = Guid.TryParse(adminIdClaim, out var parsedAdminId) ? parsedAdminId : null;
        flag.UpdatedAt = DateTime.UtcNow;

        await unitOfWork.AppFeatureFlags.UpdateAsync(flag);
        await unitOfWork.SaveChangesAsync();

        cache.Remove(ConfigHandlers.PaymentFeatureCacheKey);

        return OkResponse(new
        {
            isEnabled = flag.IsEnabled,
            freeDurationDays = flag.FreeDurationDays ?? 30,
            updatedByAdminId = flag.UpdatedByAdminId,
            updatedAt = flag.UpdatedAt,
            reason = flag.Reason,
        });
    }

    public static async Task<IResult> GetUsers(
        ApplicationDbContext db,
        int page = 1,
        int pageSize = 20,
        bool? isActive = null,
        string? search = null,
        Guid? districtId = null,
        Guid? cityId = null)
    {
        pageSize = Math.Clamp(pageSize, 1, 100);
        page = Math.Max(1, page);

        var query = db.Users.AsQueryable();

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

        var projected = query.Select(u => new
        {
            User = u,
            TotalListings = db.RoomListings.Count(l => l.UserId == u.Id && !l.IsDeleted),
            ActiveListings = db.RoomListings.Count(l => l.UserId == u.Id && !l.IsDeleted && l.IsActive),
            TotalPlotListings = db.PlotListings.Count(l => l.UserId == u.Id && !l.IsDeleted),
            ActivePlotListings = db.PlotListings.Count(l => l.UserId == u.Id && !l.IsDeleted && l.IsActive),
            WalletBalance = db.Wallets.Where(w => w.UserId == u.Id).Select(w => (int?)w.Balance).FirstOrDefault() ?? 0,
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
                    WalletBalance = x.WalletBalance,
                };
            });

        return OkResponse(result);
    }

    public static async Task<IResult> GetDeletedAccountsByPhone(ApplicationDbContext db, string? phone = null)
    {
        if (string.IsNullOrWhiteSpace(phone)) return BadRequestResponse("phone is required");

        var records = await db.DeletedAccountRecords
            .Where(r => r.PhoneNumber.Contains(phone.Trim()))
            .OrderByDescending(r => r.DeletedAt)
            .ToListAsync();

        return OkResponse(records.Adapt<List<DeletedAccountRecordDto>>());
    }

    public static async Task<IResult> GetUserById(Guid id, ApplicationDbContext db)
    {
        var u = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id);
        if (u == null) return NotFoundResponse("User not found");

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
            WalletBalance = await db.Wallets.Where(w => w.UserId == id).Select(w => (int?)w.Balance).FirstOrDefaultAsync() ?? 0,
        };

        return OkResponse(dto);
    }

    public static async Task<IResult> UpdateUserStatus(
        Guid id,
        UpdateUserStatusRequest request,
        ApplicationDbContext db)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id);
        if (user == null) return NotFoundResponse("User not found");

        user.IsActive = request.IsActive;
        user.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        // Suspending is pure moderation (always allowed, mirrors ToggleAdminListingStatus/
        // AdminTogglePlotListing deactivate). Un-suspending must NOT resurrect a listing for free —
        // same "no free-activation bypass around the credit-spend engine" rule as those two endpoints,
        // just applied in bulk here: only listings still within their own paid ValidUntil window come
        // back. Room and Plot are swept symmetrically — previously only Room was touched here. Bulk
        // ExecuteUpdateAsync (not load-then-loop-then-save), matching this repo's convention for
        // atomic mass updates (see WalletRepository, CreditPackPurchaseRepository).
        var now = DateTime.UtcNow;
        var roomQuery = db.RoomListings.Where(l => l.UserId == id && !l.IsDeleted);
        var plotQuery = db.PlotListings.Where(l => l.UserId == id && !l.IsDeleted);
        if (request.IsActive)
        {
            roomQuery = roomQuery.Where(l => l.ValidUntil.HasValue && l.ValidUntil > now);
            plotQuery = plotQuery.Where(l => l.ValidUntil.HasValue && l.ValidUntil > now);
        }

        await roomQuery.ExecuteUpdateAsync(s => s
            .SetProperty(l => l.IsActive, request.IsActive)
            .SetProperty(l => l.UpdatedAt, now));
        await plotQuery.ExecuteUpdateAsync(s => s
            .SetProperty(l => l.IsActive, request.IsActive)
            .SetProperty(l => l.UpdatedAt, now));

        return OkResponse(new { id = user.Id, isActive = user.IsActive });
    }

    // ── Wallet Ledger ───────────────────────────────────────────────────────────
    // Admin-wide view — keyset/cursor-paginated (not the repo's usual offset Skip/Take), since this
    // scans across every user's combined transactions and deep OFFSET degrades badly at scale.
    // See ICreditTransactionRepository.GetKeysetPagedWithUserAsync and the design doc §4b.

    public static async Task<IResult> GetWalletTransactions(
        IUnitOfWork unitOfWork, int pageSize = 20,
        DateTime? afterCreatedAt = null, Guid? afterId = null,
        Guid? userId = null, string? reason = null)
    {
        pageSize = Math.Clamp(pageSize, 1, 100);
        var normalizedReason = (!string.IsNullOrWhiteSpace(reason) && reason.ToUpper() != "ALL") ? reason.ToUpper() : null;

        var (items, hasMore) = await unitOfWork.CreditTransactions.GetKeysetPagedWithUserAsync(
            pageSize, afterCreatedAt, afterId, userId, normalizedReason);

        return OkResponse(new { items, hasMore });
    }

    // ── Manual Wallet Credit/Debit ─────────────────────────────────────────────
    // The generic admin lever this session settled on — no per-listing "grant membership" bypass;
    // admin credits/debits the wallet, the owner spends it through the normal Go-Live flow.

    // Best-effort — a SignalR push failure must never turn an already-committed wallet
    // credit/debit into an error response. Targets user_{id} — the user AFFECTED by the admin
    // action, never the acting admin's own id.
    private static async Task PushWalletBalanceChangedAsync(IHubContext<WalletHub> hubContext, Guid userId, int balance, string reason)
    {
        try
        {
            await hubContext.Clients.Group($"user_{userId}").SendAsync("WalletBalanceChanged", new
            {
                balance,
                reason,
                occurredAt = DateTime.UtcNow,
            });
        }
        catch { }
    }

    public static async Task<IResult> CreditUserWallet(
        Guid id, ManualWalletAdjustmentRequest request, IValidator<ManualWalletAdjustmentRequest> validator,
        IUnitOfWork unitOfWork, ICreditWalletService wallet, ClaimsPrincipal principal, IHubContext<WalletHub> hubContext)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);
        if (await unitOfWork.Users.GetByIdAsync(id) == null) return NotFoundResponse("User not found");
        if (!UsersHandlers.TryGetUserId(principal, out var adminId)) return UnauthorizedResponse();

        var result = await wallet.AddCreditsAsync(id, request.Amount, CreditTransactionReasons.AdminCredit,
            referenceId: request.IdempotencyKey, performedByUserId: adminId, note: request.Reason);

        // Success or AlreadyCredited (idempotent replay of the same IdempotencyKey) both report the
        // current balance as a success — the caller cannot tell the difference, by design. Same
        // reasoning applies to the push: a redundant push on a replay is a harmless client-side no-op.
        await PushWalletBalanceChangedAsync(hubContext, id, result.BalanceAfter, CreditTransactionReasons.AdminCredit);
        return OkResponse(new { success = true, newBalance = result.BalanceAfter });
    }

    public static async Task<IResult> DebitUserWallet(
        Guid id, ManualWalletAdjustmentRequest request, IValidator<ManualWalletAdjustmentRequest> validator,
        IUnitOfWork unitOfWork, ICreditWalletService wallet, ClaimsPrincipal principal, IHubContext<WalletHub> hubContext)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);
        if (await unitOfWork.Users.GetByIdAsync(id) == null) return NotFoundResponse("User not found");
        if (!UsersHandlers.TryGetUserId(principal, out var adminId)) return UnauthorizedResponse();

        var result = await wallet.SpendCreditsAsync(id, request.Amount, CreditTransactionReasons.AdminDebit,
            referenceId: request.IdempotencyKey, performedByUserId: adminId, note: request.Reason);

        if (result.Outcome == CreditSpendOutcome.InsufficientBalance)
            return BadRequestResponse("User has insufficient balance for this debit.");

        await PushWalletBalanceChangedAsync(hubContext, id, result.BalanceAfter, CreditTransactionReasons.AdminDebit);
        return OkResponse(new { success = true, newBalance = result.BalanceAfter });
    }

    // ── Go-Live Plans (Room/Plot) ──────────────────────────────────────────────
    // One shared implementation per operation, discriminated by CreditFeature.Key, backed by the
    // shared CreditPlan table (replaces the old RoomPlan/PlotPlan tables — literal leftovers from the
    // real-money membership system that were never rebuilt as a dedicated credit-era entity). CreditPlan
    // is deliberately feature-agnostic — Room/Plot Go-Live today, any future credit-gated feature
    // (e.g. contact reveal, chat) slots in as a new CreditFeature key with zero schema change. The old
    // per-kind copy-pasted methods had actually drifted apart (the Plot endpoint's validation errors
    // said "RoomPlan" instead of a plot-appropriate message) — this shape makes that class of bug
    // structurally impossible, since there's only one place each message is built now.

    public static async Task<IResult> GetPlans(IUnitOfWork unitOfWork) =>
        OkResponse(await GetCreditPlansCoreAsync(unitOfWork, CreditFeatureKeys.RoomGoLive, "roomLimit"));

    public static async Task<IResult> GetPlotPlans(IUnitOfWork unitOfWork) =>
        OkResponse(await GetCreditPlansCoreAsync(unitOfWork, CreditFeatureKeys.PlotGoLive, "plotLimit"));

    private static async Task<List<object>> GetCreditPlansCoreAsync(IUnitOfWork unitOfWork, string featureKey, string limitKey)
    {
        var plans = await unitOfWork.CreditPlans.GetByFeatureKeyAsync(featureKey);
        return plans.OrderBy(p => p.Price).Select(p => ToAdminCreditPlanDto(p, limitKey)).ToList();
    }

    private static object ToAdminCreditPlanDto(CreditPlan p, string limitKey) => new Dictionary<string, object?>
    {
        ["id"] = p.Id,
        ["planType"] = p.PlanType,
        ["days"] = p.Days,
        ["price"] = p.Price,
        ["originalPrice"] = p.OriginalPrice,
        ["discountPercent"] = p.DiscountPercent,
        [limitKey] = p.Quota,
        ["isFeatured"] = p.IsFeatured,
        ["isEnabled"] = p.IsEnabled,
    };

    public static async Task<IResult> CreatePlan(CreateRoomCreditPlanRequest request, IUnitOfWork unitOfWork) =>
        await CreateCreditPlanCoreAsync(unitOfWork, CreditFeatureKeys.RoomGoLive, "roomLimit", "Room", request.PlanType,
            request.Price, request.Days, request.ListingLimit, request.OriginalPrice, request.DiscountPercent,
            request.IsFeatured, "/api/v1/admin/plans");

    public static async Task<IResult> CreatePlotPlan(CreatePlotCreditPlanRequest request, IUnitOfWork unitOfWork) =>
        await CreateCreditPlanCoreAsync(unitOfWork, CreditFeatureKeys.PlotGoLive, "plotLimit", "Plot", request.PlanType,
            request.Price, request.Days, request.ListingLimit, request.OriginalPrice, request.DiscountPercent,
            request.IsFeatured, "/api/v1/admin/plot-plans");

    private static async Task<IResult> CreateCreditPlanCoreAsync(
        IUnitOfWork unitOfWork, string featureKey, string limitKey, string unitNoun, string rawPlanType,
        int price, int days, int quota, int originalPrice, int discountPercent, bool isFeatured,
        string locationPrefix)
    {
        if (string.IsNullOrWhiteSpace(rawPlanType))
            return BadRequestResponse("Plan type name is required.");
        if (price < 0)
            return BadRequestResponse("Price cannot be negative.");
        if (days <= 0)
            return BadRequestResponse("Days must be greater than 0.");
        if (quota <= 0)
            return BadRequestResponse($"{unitNoun} limit must be greater than 0.");

        var key = rawPlanType.Trim().ToUpperInvariant();
        if (await unitOfWork.CreditPlans.GetByFeatureKeyAndPlanTypeAsync(featureKey, key) != null)
            return BadRequestResponse($"Plan '{key}' already exists.", "DuplicatePlan");

        var plan = new CreditPlan
        {
            Id = Guid.NewGuid(),
            FeatureKey = featureKey,
            PlanType = key,
            Price = price,
            Days = days,
            Quota = quota,
            OriginalPrice = originalPrice,
            DiscountPercent = discountPercent,
            IsFeatured = isFeatured,
            IsEnabled = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        await unitOfWork.CreditPlans.AddAsync(plan);
        await unitOfWork.SaveChangesAsync();

        return CreatedResponse(ToAdminCreditPlanDto(plan, limitKey), $"{locationPrefix}/{plan.Id}");
    }

    public static async Task<IResult> UpdatePlan(Guid id, UpdateRoomCreditPlanRequest request, IUnitOfWork unitOfWork) =>
        await UpdateCreditPlanCoreAsync(unitOfWork, CreditFeatureKeys.RoomGoLive, "roomLimit", "Room", id, request.Days,
            request.Price, request.ListingLimit, request.IsEnabled, request.OriginalPrice, request.DiscountPercent,
            request.IsFeatured);

    public static async Task<IResult> UpdatePlotPlan(Guid id, UpdatePlotCreditPlanRequest request, IUnitOfWork unitOfWork) =>
        await UpdateCreditPlanCoreAsync(unitOfWork, CreditFeatureKeys.PlotGoLive, "plotLimit", "Plot", id, request.Days,
            request.Price, request.ListingLimit, request.IsEnabled, request.OriginalPrice, request.DiscountPercent,
            request.IsFeatured);

    private static async Task<IResult> UpdateCreditPlanCoreAsync(
        IUnitOfWork unitOfWork, string featureKey, string limitKey, string unitNoun, Guid id,
        int? days, int? price, int? quota, bool? isEnabled, int? originalPrice, int? discountPercent, bool? isFeatured)
    {
        var plan = await unitOfWork.CreditPlans.GetByIdAsync(id);
        // FeatureKey guard matters now that every credit-gated feature shares one table — a Room admin
        // request must not be able to mutate a Plot row (or vice versa) just by guessing/reusing an id.
        if (plan == null || plan.FeatureKey != featureKey)
            return NotFoundResponse("Plan not found");

        if (days.HasValue)
        {
            if (days.Value <= 0) return BadRequestResponse("Days must be greater than 0");
            plan.Days = days.Value;
        }
        if (price.HasValue)
        {
            if (price.Value < 0) return BadRequestResponse("Price cannot be negative");
            plan.Price = price.Value;
        }
        if (quota.HasValue)
        {
            if (quota.Value <= 0) return BadRequestResponse($"{unitNoun} limit must be greater than 0");
            plan.Quota = quota.Value;
        }
        if (isEnabled.HasValue) plan.IsEnabled = isEnabled.Value;
        if (originalPrice.HasValue) plan.OriginalPrice = originalPrice.Value;
        if (discountPercent.HasValue) plan.DiscountPercent = discountPercent.Value;
        if (isFeatured.HasValue) plan.IsFeatured = isFeatured.Value;
        plan.UpdatedAt = DateTime.UtcNow;

        await unitOfWork.CreditPlans.UpdateAsync(plan);
        await unitOfWork.SaveChangesAsync();

        return OkResponse(ToAdminCreditPlanDto(plan, limitKey));
    }

    // ── Coupons ─────────────────────────────────────────────────────────────────
    // Full push-screen form on the admin side (more fields than a bottom sheet, and Status
    // genuinely changes over a coupon's lifetime, unlike a Plan) — see design doc §5.

    public static async Task<IResult> GetCoupons(
        IUnitOfWork unitOfWork, int page = 1, int pageSize = 20, string? status = null, string? search = null)
    {
        pageSize = Math.Clamp(pageSize, 1, 100);
        page = Math.Max(1, page);
        var normalizedStatus = (!string.IsNullOrWhiteSpace(status) && status.ToUpper() != "ALL") ? status : null;

        var (items, total) = await unitOfWork.Coupons.GetPagedAsync(normalizedStatus, triggerType: null, search, page, pageSize);
        var dtos = items.Select(c => new CouponDto
        {
            Id = c.Id, Code = c.Code, CreditValue = c.CreditValue, TriggerType = c.TriggerType,
            PerUserLimit = c.PerUserLimit, MaxTotalRedemptions = c.MaxTotalRedemptions,
            CurrentRedemptions = c.CurrentRedemptions, ValidFrom = c.ValidFrom, ValidUntil = c.ValidUntil,
            Status = c.Status, CampaignLabel = c.CampaignLabel, CreatedAt = c.CreatedAt,
        }).ToList();

        return OkResponse(new { items = dtos, hasMore = (page * pageSize) < total, totalCount = total });
    }

    public static async Task<IResult> GetCouponById(Guid id, IUnitOfWork unitOfWork)
    {
        var coupon = await unitOfWork.Coupons.GetByIdAsync(id);
        if (coupon == null) return NotFoundResponse("Coupon not found");

        return OkResponse(new CouponDto
        {
            Id = coupon.Id, Code = coupon.Code, CreditValue = coupon.CreditValue, TriggerType = coupon.TriggerType,
            PerUserLimit = coupon.PerUserLimit, MaxTotalRedemptions = coupon.MaxTotalRedemptions,
            CurrentRedemptions = coupon.CurrentRedemptions, ValidFrom = coupon.ValidFrom, ValidUntil = coupon.ValidUntil,
            Status = coupon.Status, CampaignLabel = coupon.CampaignLabel, CreatedAt = coupon.CreatedAt,
        });
    }

    public static async Task<IResult> CreateCoupon(
        CreateCouponRequest request, IValidator<CreateCouponRequest> validator,
        IUnitOfWork unitOfWork, ClaimsPrincipal principal)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);
        if (!UsersHandlers.TryGetUserId(principal, out var adminId)) return UnauthorizedResponse();

        // Retry a fresh code on the (very unlikely) chance of a collision against the partial
        // unique index on Code — DetachTracked clears the failed candidate before regenerating.
        const int maxAttempts = 5;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var coupon = new Coupon
            {
                Id = Guid.NewGuid(),
                Code = CouponCodeGenerator.Generate(),
                CreditValue = request.CreditValue,
                TriggerType = request.TriggerType,
                PerUserLimit = 1,
                MaxTotalRedemptions = request.MaxTotalRedemptions,
                CurrentRedemptions = 0,
                ValidFrom = request.ValidFrom,
                ValidUntil = request.ValidUntil,
                Status = CouponStatuses.Active,
                CreatedBy = adminId,
                CampaignLabel = request.CampaignLabel,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };

            await unitOfWork.Coupons.AddAsync(coupon);
            try
            {
                await unitOfWork.SaveChangesAsync();
                return CreatedResponse(new CouponDto
                {
                    Id = coupon.Id, Code = coupon.Code, CreditValue = coupon.CreditValue, TriggerType = coupon.TriggerType,
                    PerUserLimit = coupon.PerUserLimit, MaxTotalRedemptions = coupon.MaxTotalRedemptions,
                    CurrentRedemptions = coupon.CurrentRedemptions, ValidFrom = coupon.ValidFrom, ValidUntil = coupon.ValidUntil,
                    Status = coupon.Status, CampaignLabel = coupon.CampaignLabel, CreatedAt = coupon.CreatedAt,
                }, $"/api/v1/admin/coupons/{coupon.Id}");
            }
            catch (DbUpdateException) when (attempt < maxAttempts)
            {
                unitOfWork.Coupons.DetachTracked(coupon);
            }
        }

        return ServerErrorResponse();
    }

    public static async Task<IResult> UpdateCoupon(
        Guid id, UpdateCouponRequest request, IValidator<UpdateCouponRequest> validator, IUnitOfWork unitOfWork)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var coupon = await unitOfWork.Coupons.GetByIdAsync(id);
        if (coupon == null) return NotFoundResponse("Coupon not found");

        if (!request.ClearMaxTotalRedemptions && request.MaxTotalRedemptions.HasValue
            && request.MaxTotalRedemptions.Value < coupon.CurrentRedemptions)
            return BadRequestResponse($"MaxTotalRedemptions cannot be less than the {coupon.CurrentRedemptions} redemptions already used.");

        coupon.MaxTotalRedemptions = request.ClearMaxTotalRedemptions
            ? null
            : request.MaxTotalRedemptions ?? coupon.MaxTotalRedemptions;
        coupon.ValidUntil = request.ClearValidUntil ? null : request.ValidUntil ?? coupon.ValidUntil;
        if (request.CreditValue.HasValue) coupon.CreditValue = request.CreditValue.Value;
        if (request.CampaignLabel != null) coupon.CampaignLabel = request.CampaignLabel;

        if (request.Status != null)
        {
            coupon.Status = request.Status;
        }
        else if (coupon.Status == CouponStatuses.Exhausted
            && (coupon.MaxTotalRedemptions == null || coupon.CurrentRedemptions < coupon.MaxTotalRedemptions.Value))
        {
            // Raising (or clearing) the limit past current usage lifts an Exhausted coupon back to
            // Active automatically, mirroring TryReserveRedemptionSlotAsync's exhaustion condition in
            // reverse — only when the admin hasn't already explicitly requested a different Status
            // in this same call, so an explicit choice always wins over auto-reconciliation.
            coupon.Status = CouponStatuses.Active;
        }

        coupon.UpdatedAt = DateTime.UtcNow;

        await unitOfWork.Coupons.UpdateAsync(coupon);
        await unitOfWork.SaveChangesAsync();

        return OkResponse(new CouponDto
        {
            Id = coupon.Id, Code = coupon.Code, CreditValue = coupon.CreditValue, TriggerType = coupon.TriggerType,
            PerUserLimit = coupon.PerUserLimit, MaxTotalRedemptions = coupon.MaxTotalRedemptions,
            CurrentRedemptions = coupon.CurrentRedemptions, ValidFrom = coupon.ValidFrom, ValidUntil = coupon.ValidUntil,
            Status = coupon.Status, CampaignLabel = coupon.CampaignLabel, CreatedAt = coupon.CreatedAt,
        });
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

    public static async Task<IResult> ToggleAdminListingStatus(
        Guid id, AdminToggleListingRequest request, ApplicationDbContext db, IServiceProvider sp)
    {
        var listing = await db.RoomListings.FindAsync(id);
        if (listing == null || listing.IsDeleted) return NotFoundResponse("RoomListing not found");

        // No membership to check anymore — and admin does not get a free-activation bypass around
        // the credit-spend engine. Activating requires the listing to already be within a paid window
        // (the owner's own Go-Live, or a free reactivation of one); if not, admin's lever is to
        // credit the owner's wallet (POST /users/{id}/wallet/credit) so THEY can go live, not to
        // flip this flag for free. Deactivate is always allowed — pure moderation power, no check.
        if (request.IsActive)
        {
            var stillWithinValidity = listing.ValidUntil.HasValue && listing.ValidUntil > DateTime.UtcNow;
            if (!stillWithinValidity)
                return BadRequestResponse("This listing has no valid paid period. Ask the owner to Go Live, or add credits to their wallet so they can.");
        }

        listing.IsActive = request.IsActive;
        listing.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        // Fixes the pre-existing asymmetry where the plot version invalidated this cache and the
        // room version didn't — both now do, on any state change.
        var redis = sp.GetService<IConnectionMultiplexer>();
        if (redis != null)
        {
            try
            {
                var cacheDb = redis.GetDatabase();
                var server = redis.GetServers().FirstOrDefault(s => s.IsConnected);
                if (server != null)
                    await foreach (var key in server.KeysAsync(pattern: $"nearby:{listing.DistrictId}:*"))
                        await cacheDb.KeyDeleteAsync(key);
            }
            catch { }
        }
        // Home's "Recently added" and "Rooms for you" feeds are cached separately from the
        // district-scoped nearby cache above — bust them whenever a listing leaves the active set
        // so a moderation-deactivated listing doesn't linger as a tappable, soon-to-404 card.
        if (!request.IsActive)
        {
            await InvalidateRecentRoomsCacheAsync(redis);
            await InvalidateForYouRoomsCacheAsync(redis, listing.DistrictId);
        }

        return OkResponse(new { success = true, isActive = listing.IsActive });
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
        Guid id, IUnitOfWork unitOfWork, IPhotoService photoService, ICreditWalletService wallet, IServiceProvider sp)
    {
        // Tracked fetch (not the AsNoTracking GetByIdWithPhotosForAdminAsync) — this listing gets
        // mutated and saved below, and RoomListing/PlotListing use a shadow xmin concurrency token
        // that only round-trips correctly through a tracked entity (mirrors the owner delete path,
        // RoomListingsHandlers.DeleteListing, which uses the same tracked GetByIdAsync for the same
        // reason). Bulk-deletes the whole listing's photo folder like the owner path does, so no
        // Photos Include is needed here either.
        var listing = await unitOfWork.RoomListings.GetByIdAsync(id);
        if (listing == null || listing.IsDeleted) return NotFoundResponse("RoomListing not found");

        await photoService.DeleteRoomPhotosAsync(listing.UserId, id);

        // A still-Pending Go-Live request (never approved/activated) with credits already spent
        // must be refunded on delete — mirrors GoLiveHandlers's own transaction shape since folding
        // a credit mutation into a bare SaveChangesAsync would be a correctness gap.
        var needsRefund = listing.LiveRequestStatus == GoLiveRequestStatuses.Pending
            && listing.RequestedPlanCreditsSpent is > 0;

        listing.IsDeleted = true;
        listing.DeletedAt = DateTime.UtcNow;
        await unitOfWork.RoomListings.UpdateAsync(listing);

        if (needsRefund)
        {
            await unitOfWork.BeginTransactionAsync();
            try
            {
                await wallet.AddCreditsAsync(listing.UserId, listing.RequestedPlanCreditsSpent!.Value, CreditTransactionReasons.GoLivePendingRefund, listing.Id);
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
            }
            catch
            {
                await unitOfWork.RollbackTransactionAsync();
                throw;
            }
        }
        else
        {
            await unitOfWork.SaveChangesAsync();
        }

        var redis = sp.GetService<IConnectionMultiplexer>();
        await InvalidateNearbyCacheAsync(redis, listing.DistrictId);
        await InvalidateRecentRoomsCacheAsync(redis);
        await InvalidateForYouRoomsCacheAsync(redis, listing.DistrictId);

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

        var roomsById = roomIds.Count == 0 ? new Dictionary<Guid, RoomListing>() : await db.RoomListings
            .AsNoTracking().Include(l => l.RoomType).Include(l => l.District).Include(l => l.City)
            .Where(l => roomIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id);
        var plotsById = plotIds.Count == 0 ? new Dictionary<Guid, PlotListing>() : await db.PlotListings
            .AsNoTracking().Include(p => p.PlotType).Include(p => p.District).Include(p => p.City)
            .Where(p => plotIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id);

        var items = paged.Items.Select(r =>
        {
            var dto = ToAdminListingReportDto(r);
            if (r.ListingType == ListingKinds.Room && roomsById.TryGetValue(r.ListingId, out var room)) ApplyRoomReportDetails(dto, room);
            else if (r.ListingType == ListingKinds.Plot && plotsById.TryGetValue(r.ListingId, out var plot)) ApplyPlotReportDetails(dto, plot);
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
    // point: a report resolution. Unlike a Go-Live-moderation rejection (content never went live,
    // credits refunded), a report-driven rejection targets a listing that WAS already live and
    // already delivered value — no refund here, deliberately, unlike RejectRoomGoLiveRequest/
    // RejectPlotGoLiveRequest which this otherwise mirrors exactly (transaction shape,
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
        IHubContext<InquiryHub> hubContext, IRabbitMqPublisher publisher)
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
            ListingKinds.Room => await RejectReportedRoomAsync(report, request.Reason, adminId, unitOfWork, db, sp, hubContext, publisher),
            ListingKinds.Plot => await PlotListingHandlers.RejectReportedPlotAsync(report, request.Reason, adminId, unitOfWork, db, sp, hubContext, publisher),
            _ => BadRequestResponse($"Unknown listing type '{report.ListingType}' on this report"),
        };
    }

    private static async Task<IResult> RejectReportedRoomAsync(
        ListingReport report, string reason, Guid? adminId,
        IUnitOfWork unitOfWork, ApplicationDbContext db, IServiceProvider sp,
        IHubContext<InquiryHub> hubContext, IRabbitMqPublisher publisher)
    {
        var listing = await unitOfWork.RoomListings.GetByIdAsync(report.ListingId);
        if (listing == null || listing.IsDeleted) return NotFoundResponse("RoomListing not found");

        // No wallet.AddCreditsAsync here — deliberately different from RejectRoomGoLiveRequest, which
        // refunds a not-yet-live submission. This listing was already live and already delivered
        // value, so a report-driven takedown must never refund credits.
        await unitOfWork.BeginTransactionAsync();
        try
        {
            listing.IsActive = false;
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

            await unitOfWork.CommitTransactionAsync();

            var redis = sp.GetService<IConnectionMultiplexer>();
            await InvalidateNearbyCacheAsync(redis, listing.DistrictId);
            await InvalidateRecentRoomsCacheAsync(redis);
            await InvalidateForYouRoomsCacheAsync(redis, listing.DistrictId);

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
        RequestedPlanType = l.RequestedPlanType,
        RequestedPlanDays = l.RequestedPlanDays,
        RequestedPlanCreditsSpent = l.RequestedPlanCreditsSpent,
        SubmittedAt = l.UpdatedAt,
        Photos = includePhotos ? l.Photos.Select(p => p.PhotoUrl).ToList() : new List<string>(),
        Address = l.Address,
        Description = l.Description,
        DistrictName = l.District?.Name,
        CityName = l.City?.Name,
        RoomTypeName = l.RoomType?.Name,
        FurnishedStatus = l.FurnishedStatus,
        PriceMonthly = l.PriceMonthly,
    };

    public static async Task<IResult> GetRoomGoLiveRequests(
        ApplicationDbContext db, int page = 1, int pageSize = 20, string? status = "Pending")
    {
        pageSize = Math.Clamp(pageSize, 1, 100);
        page = Math.Max(1, page);

        // Deleted listings never leave LiveRequestStatus behind — excluded here the same way
        // GetAdminListings excludes them, so a deleted-while-Pending listing can't linger in this
        // queue forever.
        var query = db.RoomListings
            .Include(l => l.User)
            .Include(l => l.RoomType)
            .Include(l => l.District)
            .Include(l => l.City)
            .Where(l => !l.IsDeleted && l.LiveRequestStatus == status)
            .OrderByDescending(l => l.UpdatedAt);

        var result = await query.ToPagedResultAsync(page, pageSize, l => ToGoLiveRequestDto(l, includePhotos: false));
        return OkResponse(new { items = result.Items, hasMore = result.HasMore });
    }

    public static async Task<IResult> GetRoomGoLiveRequestById(Guid id, ApplicationDbContext db)
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
        return OkResponse(ToGoLiveRequestDto(listing, includePhotos: true));
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

    // Clone of InquiryHandlers.SendNotificationEventsAsync's single-notification shape — SignalR
    // user_{id} push via InquiryHub (the same hub every other generic NotificationEvent producer
    // reuses) + RabbitMQ notification.push publish for NotificationPushWorkerService. Best-effort:
    // a push failure must never turn an already-committed Approve/Reject into an error response.
    private static async Task SendGoLiveNotificationAsync(
        IHubContext<InquiryHub> hubContext, IRabbitMqPublisher publisher, NotificationEvent notification)
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

    public static async Task<IResult> ApproveRoomGoLiveRequest(
        Guid id, IUnitOfWork unitOfWork, ApplicationDbContext db, IServiceProvider sp,
        IHubContext<InquiryHub> hubContext, IRabbitMqPublisher publisher)
    {
        var listing = await unitOfWork.RoomListings.GetByIdAsync(id);
        if (listing == null || listing.IsDeleted) return NotFoundResponse("RoomListing not found");
        if (listing.LiveRequestStatus != GoLiveRequestStatuses.Pending)
            return BadRequestResponse("This request has already been resolved");

        await unitOfWork.BeginTransactionAsync();
        try
        {
            // CRITICAL: ValidUntil comes from the snapshot taken at request time
            // (RequestedPlanDays), never by re-resolving CreditPlan — the catalog is admin-mutable
            // and could have changed/been disabled between request and approval.
            listing.LiveRequestStatus = GoLiveRequestStatuses.Approved;
            listing.IsActive = true;
            listing.ValidUntil = DateTime.UtcNow.AddDays(listing.RequestedPlanDays!.Value);
            listing.UpdatedAt = DateTime.UtcNow;
            await unitOfWork.RoomListings.UpdateAsync(listing);

            var notification = BuildRoomGoLiveApprovedNotification(listing.UserId);
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

            var redis = sp.GetService<IConnectionMultiplexer>();
            await InvalidateNearbyCacheAsync(redis, listing.DistrictId);
            await InvalidateRecentRoomsCacheAsync(redis);
            await InvalidateForYouRoomsCacheAsync(redis, listing.DistrictId);

            await SendGoLiveNotificationAsync(hubContext, publisher, notification);

            return OkResponse(new { success = true, isActive = true, validUntil = listing.ValidUntil });
        }
        catch
        {
            await unitOfWork.RollbackTransactionAsync();
            throw;
        }
    }

    public static async Task<IResult> RejectRoomGoLiveRequest(
        Guid id, RejectGoLiveRequest request, IValidator<RejectGoLiveRequest> validator,
        IUnitOfWork unitOfWork, ApplicationDbContext db, ICreditWalletService wallet,
        IHubContext<InquiryHub> hubContext, IRabbitMqPublisher publisher)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var listing = await unitOfWork.RoomListings.GetByIdAsync(id);
        if (listing == null || listing.IsDeleted) return NotFoundResponse("RoomListing not found");
        if (listing.LiveRequestStatus != GoLiveRequestStatuses.Pending)
            return BadRequestResponse("This request has already been resolved");

        // Same refund-transaction shape as DeleteAdminListing — folding a credit mutation into a
        // bare SaveChangesAsync would be a correctness gap.
        await unitOfWork.BeginTransactionAsync();
        try
        {
            listing.LiveRequestStatus = GoLiveRequestStatuses.Rejected;
            listing.RejectedReason = request.Reason;
            listing.UpdatedAt = DateTime.UtcNow;
            await unitOfWork.RoomListings.UpdateAsync(listing);

            if (listing.RequestedPlanCreditsSpent is > 0)
                await wallet.AddCreditsAsync(listing.UserId, listing.RequestedPlanCreditsSpent.Value, CreditTransactionReasons.GoLivePendingRefund, listing.Id);

            var notification = BuildRoomGoLiveRejectedNotification(listing.UserId, request.Reason);
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

            await SendGoLiveNotificationAsync(hubContext, publisher, notification);

            return OkResponse(new { success = true });
        }
        catch
        {
            await unitOfWork.RollbackTransactionAsync();
            throw;
        }
    }
}
