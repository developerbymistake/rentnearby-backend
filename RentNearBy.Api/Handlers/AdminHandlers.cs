using FluentValidation;
using Mapster;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
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

        // Real-money earnings now come from coin-pack purchases only — Go Live itself spends coins,
        // not rupees, so PaymentTransaction (deleted with the old membership system) is no longer
        // the revenue source.
        var now = DateTime.UtcNow;
        var totalEarnings = await db.CoinPackPurchases
            .Where(p => p.Status == CoinPackPurchaseStatuses.Success)
            .SumAsync(p => (int?)p.PriceInr) ?? 0;
        var currentMonthEarnings = await db.CoinPackPurchases
            .Where(p => p.Status == CoinPackPurchaseStatuses.Success && p.CompletedAt.HasValue &&
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

    // ── Coin Packs ──────────────────────────────────────────────────────────────
    // Mirrors the Room/Plot Plans pattern below: IsEnabled toggle, no delete, bottom-sheet-sized form.

    public static async Task<IResult> GetCoinPacks(IUnitOfWork unitOfWork)
    {
        var packs = await unitOfWork.CoinPacks.GetAllAsync();
        return OkResponse(packs.OrderBy(p => p.SortOrder).Select(p => new CoinPackDto
        {
            Id = p.Id, Coins = p.Coins, BonusCoins = p.BonusCoins, PriceInr = p.PriceInr,
            IsEnabled = p.IsEnabled, SortOrder = p.SortOrder, IsFeatured = p.IsFeatured,
        }));
    }

    public static async Task<IResult> CreateCoinPack(
        CreateCoinPackRequest request, IValidator<CreateCoinPackRequest> validator,
        IUnitOfWork unitOfWork, IMemoryCache cache)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var pack = new CoinPack
        {
            Id = Guid.NewGuid(), Coins = request.Coins, BonusCoins = request.BonusCoins,
            PriceInr = request.PriceInr, SortOrder = request.SortOrder, IsFeatured = request.IsFeatured,
            IsEnabled = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        await unitOfWork.CoinPacks.AddAsync(pack);
        await unitOfWork.SaveChangesAsync();

        cache.Remove(CoinPackHandlers.ActivePacksCacheKey);

        return CreatedResponse(new CoinPackDto
        {
            Id = pack.Id, Coins = pack.Coins, BonusCoins = pack.BonusCoins, PriceInr = pack.PriceInr,
            IsEnabled = pack.IsEnabled, SortOrder = pack.SortOrder, IsFeatured = pack.IsFeatured,
        }, $"/api/v1/admin/coin-packs/{pack.Id}");
    }

    public static async Task<IResult> UpdateCoinPack(
        Guid id, UpdateCoinPackRequest request, IValidator<UpdateCoinPackRequest> validator,
        IUnitOfWork unitOfWork, IMemoryCache cache)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var pack = await unitOfWork.CoinPacks.GetByIdAsync(id);
        if (pack == null) return NotFoundResponse("Coin pack not found");

        if (request.Coins.HasValue) pack.Coins = request.Coins.Value;
        if (request.BonusCoins.HasValue) pack.BonusCoins = request.BonusCoins.Value;
        if (request.PriceInr.HasValue) pack.PriceInr = request.PriceInr.Value;
        if (request.SortOrder.HasValue) pack.SortOrder = request.SortOrder.Value;
        if (request.IsFeatured.HasValue) pack.IsFeatured = request.IsFeatured.Value;
        if (request.IsEnabled.HasValue) pack.IsEnabled = request.IsEnabled.Value;
        pack.UpdatedAt = DateTime.UtcNow;

        await unitOfWork.CoinPacks.UpdateAsync(pack);
        await unitOfWork.SaveChangesAsync();

        cache.Remove(CoinPackHandlers.ActivePacksCacheKey);

        return OkResponse(new CoinPackDto
        {
            Id = pack.Id, Coins = pack.Coins, BonusCoins = pack.BonusCoins, PriceInr = pack.PriceInr,
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

        var listings = await db.RoomListings.Where(l => l.UserId == id && !l.IsDeleted).ToListAsync();

        user.IsActive = request.IsActive;
        user.UpdatedAt = DateTime.UtcNow;

        foreach (var listing in listings)
        {
            listing.IsActive = request.IsActive;
            listing.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync();

        return OkResponse(new { id = user.Id, isActive = user.IsActive });
    }

    // ── Wallet Ledger ───────────────────────────────────────────────────────────
    // Admin-wide view — keyset/cursor-paginated (not the repo's usual offset Skip/Take), since this
    // scans across every user's combined transactions and deep OFFSET degrades badly at scale.
    // See ICoinTransactionRepository.GetKeysetPagedWithUserAsync and the design doc §4b.

    public static async Task<IResult> GetWalletTransactions(
        IUnitOfWork unitOfWork, int pageSize = 20,
        DateTime? afterCreatedAt = null, Guid? afterId = null,
        Guid? userId = null, string? reason = null)
    {
        pageSize = Math.Clamp(pageSize, 1, 100);
        var normalizedReason = (!string.IsNullOrWhiteSpace(reason) && reason.ToUpper() != "ALL") ? reason.ToUpper() : null;

        var (items, hasMore) = await unitOfWork.CoinTransactions.GetKeysetPagedWithUserAsync(
            pageSize, afterCreatedAt, afterId, userId, normalizedReason);

        return OkResponse(new { items, hasMore });
    }

    // ── Manual Wallet Credit/Debit ─────────────────────────────────────────────
    // The generic admin lever this session settled on — no per-listing "grant membership" bypass;
    // admin credits/debits the wallet, the owner spends it through the normal Go-Live flow.

    public static async Task<IResult> CreditUserWallet(
        Guid id, ManualWalletAdjustmentRequest request, IValidator<ManualWalletAdjustmentRequest> validator,
        IUnitOfWork unitOfWork, ICoinWalletService wallet, ClaimsPrincipal principal)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);
        if (await unitOfWork.Users.GetByIdAsync(id) == null) return NotFoundResponse("User not found");
        if (!UsersHandlers.TryGetUserId(principal, out var adminId)) return UnauthorizedResponse();

        var result = await wallet.CreditCoinsAsync(id, request.Amount, CoinTransactionReasons.AdminCredit,
            referenceId: request.IdempotencyKey, performedByUserId: adminId, note: request.Reason);

        // Success or AlreadyCredited (idempotent replay of the same IdempotencyKey) both report the
        // current balance as a success — the caller cannot tell the difference, by design.
        return OkResponse(new { success = true, newBalance = result.BalanceAfter });
    }

    public static async Task<IResult> DebitUserWallet(
        Guid id, ManualWalletAdjustmentRequest request, IValidator<ManualWalletAdjustmentRequest> validator,
        IUnitOfWork unitOfWork, ICoinWalletService wallet, ClaimsPrincipal principal)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);
        if (await unitOfWork.Users.GetByIdAsync(id) == null) return NotFoundResponse("User not found");
        if (!UsersHandlers.TryGetUserId(principal, out var adminId)) return UnauthorizedResponse();

        var result = await wallet.SpendCoinsAsync(id, request.Amount, CoinTransactionReasons.AdminDebit,
            referenceId: request.IdempotencyKey, performedByUserId: adminId, note: request.Reason);

        if (result.Outcome == CoinSpendOutcome.InsufficientBalance)
            return BadRequestResponse("User has insufficient balance for this debit.");

        return OkResponse(new { success = true, newBalance = result.BalanceAfter });
    }

    public static async Task<IResult> GetPlans(ApplicationDbContext db)
    {
        var plans = await db.RoomPlans
            .OrderBy(p => p.Price)
            .Select(p => new
            {
                id = p.Id,
                planType = p.PlanType,
                days = p.Days,
                price = p.Price,
                originalPrice = p.OriginalPrice,
                discountPercent = p.DiscountPercent,
                roomLimit = p.RoomLimit,
                isEnabled = p.IsEnabled,
            })
            .ToListAsync();

        return OkResponse(plans);
    }

    public static async Task<IResult> CreatePlan(
        CreatePlanRequest request,
        ApplicationDbContext db)
    {
        if (string.IsNullOrWhiteSpace(request.PlanType))
            return BadRequestResponse("RoomPlan type name is required.");
        if (request.Price < 0)
            return BadRequestResponse("Price cannot be negative.");
        if (request.Days <= 0)
            return BadRequestResponse("Days must be greater than 0.");
        if (request.RoomLimit <= 0)
            return BadRequestResponse("Room limit must be greater than 0.");

        var key = request.PlanType.Trim().ToUpperInvariant();
        if (await db.RoomPlans.AnyAsync(p => p.PlanType == key))
            return BadRequestResponse($"RoomPlan '{key}' already exists.", "DuplicatePlan");

        var plan = new RoomPlan
        {
            Id = Guid.NewGuid(),
            PlanType = key,
            Price = request.Price,
            Days = request.Days,
            RoomLimit = request.RoomLimit,
            OriginalPrice = request.OriginalPrice,
            DiscountPercent = request.DiscountPercent,
            IsEnabled = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        db.RoomPlans.Add(plan);
        await db.SaveChangesAsync();

        return CreatedResponse(new
        {
            id = plan.Id,
            planType = plan.PlanType,
            days = plan.Days,
            price = plan.Price,
            originalPrice = plan.OriginalPrice,
            discountPercent = plan.DiscountPercent,
            roomLimit = plan.RoomLimit,
            isEnabled = plan.IsEnabled,
        }, $"/api/v1/admin/plans/{plan.Id}");
    }

    public static async Task<IResult> UpdatePlan(
        Guid id,
        UpdatePlanRequest request,
        ApplicationDbContext db)
    {
        var plan = await db.RoomPlans.FindAsync(id);
        if (plan == null) return NotFoundResponse("RoomPlan not found");

        if (request.Days.HasValue)
        {
            if (request.Days.Value <= 0) return BadRequestResponse("Days must be greater than 0");
            plan.Days = request.Days.Value;
        }
        if (request.Price.HasValue)
        {
            if (request.Price.Value < 0) return BadRequestResponse("Price cannot be negative");
            plan.Price = request.Price.Value;
        }
        if (request.RoomLimit.HasValue)
        {
            if (request.RoomLimit.Value <= 0) return BadRequestResponse("Room limit must be greater than 0");
            plan.RoomLimit = request.RoomLimit.Value;
        }
        if (request.IsEnabled.HasValue)
            plan.IsEnabled = request.IsEnabled.Value;
        if (request.OriginalPrice.HasValue)
            plan.OriginalPrice = request.OriginalPrice.Value;
        if (request.DiscountPercent.HasValue)
            plan.DiscountPercent = request.DiscountPercent.Value;

        plan.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        return OkResponse(new
        {
            id = plan.Id,
            planType = plan.PlanType,
            days = plan.Days,
            price = plan.Price,
            originalPrice = plan.OriginalPrice,
            discountPercent = plan.DiscountPercent,
            roomLimit = plan.RoomLimit,
            isEnabled = plan.IsEnabled,
        });
    }


    // ── Admin PlotListing RoomPlan endpoints ─────────────────────────────────────────────

    // PlotListingLimit is bound from the wire key "plotLimit" — matching the naming this same
    // resource's own response DTOs (GetPlotPlans/CreatePlotPlan/GetPublicPlotPlans) already use, and
    // what the admin app already sends. Without this override the request/response DTOs for the same
    // resource would use two different names for the same field ("plotListingLimit" vs "plotLimit").
    public record CreatePlotPlanRequest(string PlanType, int Price, int Days, [property: JsonPropertyName("plotLimit")] int PlotListingLimit, int OriginalPrice = 0, int DiscountPercent = 0);
    public record UpdatePlotPlanRequest(int? Days, int? Price, [property: JsonPropertyName("plotLimit")] int? PlotListingLimit, bool? IsEnabled, int? OriginalPrice, int? DiscountPercent);

    public static async Task<IResult> GetPlotPlans(ApplicationDbContext db)
    {
        var plans = await db.PlotPlans
            .OrderBy(p => p.Price)
            .Select(p => new
            {
                id = p.Id,
                planType = p.PlanType,
                days = p.Days,
                price = p.Price,
                originalPrice = p.OriginalPrice,
                discountPercent = p.DiscountPercent,
                plotLimit = p.PlotListingLimit,
                isEnabled = p.IsEnabled,
            })
            .ToListAsync();
        return OkResponse(plans);
    }

    public static async Task<IResult> CreatePlotPlan(CreatePlotPlanRequest request, ApplicationDbContext db)
    {
        if (string.IsNullOrWhiteSpace(request.PlanType))
            return BadRequestResponse("RoomPlan type name is required.");
        if (request.Price < 0)
            return BadRequestResponse("Price cannot be negative.");
        if (request.Days <= 0)
            return BadRequestResponse("Days must be greater than 0.");
        if (request.PlotListingLimit <= 0)
            return BadRequestResponse("PlotListing limit must be greater than 0.");

        var key = request.PlanType.Trim().ToUpperInvariant();
        if (await db.PlotPlans.AnyAsync(p => p.PlanType == key))
            return BadRequestResponse($"RoomPlan '{key}' already exists.", "DuplicatePlan");

        var plan = new PlotPlan
        {
            Id = Guid.NewGuid(),
            PlanType = key,
            Price = request.Price,
            Days = request.Days,
            PlotListingLimit = request.PlotListingLimit,
            OriginalPrice = request.OriginalPrice,
            DiscountPercent = request.DiscountPercent,
            IsEnabled = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        db.PlotPlans.Add(plan);
        await db.SaveChangesAsync();

        return CreatedResponse(new
        {
            id = plan.Id,
            planType = plan.PlanType,
            days = plan.Days,
            price = plan.Price,
            originalPrice = plan.OriginalPrice,
            discountPercent = plan.DiscountPercent,
            plotLimit = plan.PlotListingLimit,
            isEnabled = plan.IsEnabled,
        }, $"/api/v1/admin/plot-plans/{plan.Id}");
    }

    public static async Task<IResult> UpdatePlotPlan(Guid id, UpdatePlotPlanRequest request, ApplicationDbContext db)
    {
        var plan = await db.PlotPlans.FindAsync(id);
        if (plan == null) return NotFoundResponse("PlotListing plan not found");

        if (request.Days.HasValue)
        {
            if (request.Days.Value <= 0) return BadRequestResponse("Days must be greater than 0");
            plan.Days = request.Days.Value;
        }
        if (request.Price.HasValue)
        {
            if (request.Price.Value < 0) return BadRequestResponse("Price cannot be negative");
            plan.Price = request.Price.Value;
        }
        if (request.PlotListingLimit.HasValue)
        {
            if (request.PlotListingLimit.Value <= 0) return BadRequestResponse("PlotListing limit must be greater than 0");
            plan.PlotListingLimit = request.PlotListingLimit.Value;
        }
        if (request.IsEnabled.HasValue)
            plan.IsEnabled = request.IsEnabled.Value;
        if (request.OriginalPrice.HasValue)
            plan.OriginalPrice = request.OriginalPrice.Value;
        if (request.DiscountPercent.HasValue)
            plan.DiscountPercent = request.DiscountPercent.Value;

        plan.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        return OkResponse(new
        {
            id = plan.Id,
            planType = plan.PlanType,
            days = plan.Days,
            price = plan.Price,
            originalPrice = plan.OriginalPrice,
            discountPercent = plan.DiscountPercent,
            plotLimit = plan.PlotListingLimit,
            isEnabled = plan.IsEnabled,
        });
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
            Id = c.Id, Code = c.Code, CoinValue = c.CoinValue, TriggerType = c.TriggerType,
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
            Id = coupon.Id, Code = coupon.Code, CoinValue = coupon.CoinValue, TriggerType = coupon.TriggerType,
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
                CoinValue = request.CoinValue,
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
                    Id = coupon.Id, Code = coupon.Code, CoinValue = coupon.CoinValue, TriggerType = coupon.TriggerType,
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

        if (request.MaxTotalRedemptions.HasValue && request.MaxTotalRedemptions.Value < coupon.CurrentRedemptions)
            return BadRequestResponse($"MaxTotalRedemptions cannot be less than the {coupon.CurrentRedemptions} redemptions already used.");

        if (request.CoinValue.HasValue) coupon.CoinValue = request.CoinValue.Value;
        if (request.MaxTotalRedemptions.HasValue) coupon.MaxTotalRedemptions = request.MaxTotalRedemptions.Value;
        if (request.ValidUntil.HasValue) coupon.ValidUntil = request.ValidUntil.Value;
        if (request.CampaignLabel != null) coupon.CampaignLabel = request.CampaignLabel;
        if (request.Status != null) coupon.Status = request.Status;
        coupon.UpdatedAt = DateTime.UtcNow;

        await unitOfWork.Coupons.UpdateAsync(coupon);
        await unitOfWork.SaveChangesAsync();

        return OkResponse(new CouponDto
        {
            Id = coupon.Id, Code = coupon.Code, CoinValue = coupon.CoinValue, TriggerType = coupon.TriggerType,
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
        bool? isActive = null)
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
        // the coin-spend engine. Activating requires the listing to already be within a paid window
        // (the owner's own Go-Live, or a free reactivation of one); if not, admin's lever is to
        // credit the owner's wallet (POST /users/{id}/wallet/credit) so THEY can go live, not to
        // flip this flag for free. Deactivate is always allowed — pure moderation power, no check.
        if (request.IsActive)
        {
            var stillWithinValidity = listing.ValidUntil.HasValue && listing.ValidUntil > DateTime.UtcNow;
            if (!stillWithinValidity)
                return BadRequestResponse("This listing has no valid paid period. Ask the owner to Go Live, or credit coins to their wallet so they can.");
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

        return OkResponse(new { success = true, isActive = listing.IsActive });
    }

    public static async Task<IResult> DeleteAdminListing(
        Guid id, ApplicationDbContext db, IPhotoService photoService)
    {
        var listing = await db.RoomListings
            .Include(l => l.Photos)
            .FirstOrDefaultAsync(l => l.Id == id && !l.IsDeleted);
        if (listing == null) return NotFoundResponse("RoomListing not found");

        foreach (var photo in listing.Photos)
            await photoService.DeletePhotoAsync(photo.FilePath);

        listing.IsDeleted = true;
        listing.DeletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

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

    public static async Task<IResult> GetReports(
        IUnitOfWork unitOfWork, int page = 1, int pageSize = 20, string? status = "Pending")
    {
        pageSize = Math.Clamp(pageSize, 1, 100);
        page = Math.Max(1, page);

        var paged = await unitOfWork.ListingReports.GetPagedAsync(page, pageSize, status);
        var items = paged.Items.Select(ToAdminListingReportDto).ToList();

        return OkResponse(new { items, hasMore = paged.HasMore });
    }

    public static async Task<IResult> GetReportById(Guid id, IUnitOfWork unitOfWork)
    {
        var report = await unitOfWork.ListingReports.GetByIdAsync(id);
        if (report == null) return NotFoundResponse("Report not found");
        return OkResponse(ToAdminListingReportDto(report));
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
}
