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
        ApplicationDbContext db, IOverpassService overpass, IMemoryCache cache,
        IServiceProvider sp)
    {
        var district = await db.Districts.FindAsync(id);
        if (district == null) return NotFoundResponse("District not found");

        district.IsActive = request.IsActive;
        await db.SaveChangesAsync();
        cache.Remove("districts");
        cache.Remove($"cities_{id}");
        cache.Remove("cities_all");
        await FlushContextCacheAsync(sp.GetService<IConnectionMultiplexer>());

        // Seed cities in background — don't block the response waiting for Overpass
        if (request.IsActive)
        {
            var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
            var districtName = district.Name;
            var stateName = district.StateName;
            var redis = sp.GetService<IConnectionMultiplexer>();
            _ = Task.Run(async () =>
            {
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var bgDb = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    var bgOverpass = scope.ServiceProvider.GetRequiredService<IOverpassService>();

                    var cities = await bgOverpass.FetchCitiesAsync(districtName, stateName);
                    if (cities.Count == 0) return;

                    var existingLower = (await bgDb.Cities
                        .Where(c => c.DistrictId == id)
                        .Select(c => c.Name.ToLower())
                        .ToListAsync()).ToHashSet();

                    var toAdd = cities
                        .GroupBy(c => c.Name.ToLower())
                        .Select(g => g.First())
                        .Where(c => !existingLower.Contains(c.Name.ToLower()))
                        .Select(c => new City
                        {
                            Id         = Guid.NewGuid(),
                            DistrictId = id,
                            Name       = c.Name,
                            Latitude   = (decimal)c.Lat,
                            Longitude  = (decimal)c.Lng,
                            CreatedAt  = DateTime.UtcNow,
                        }).ToList();

                    if (toAdd.Count > 0)
                    {
                        bgDb.Cities.AddRange(toAdd);
                        await bgDb.SaveChangesAsync();
                        // Flush context cache after cities are seeded so stale
                        // "no cities" entries don't persist for the full 10-min TTL
                        await FlushContextCacheAsync(redis);
                    }
                }
                catch { /* background task — swallow errors */ }
            });
        }

        return OkResponse(new { success = true, isActive = district.IsActive });
    }

    public static async Task<IResult> ForceActivateDistrict(
        Guid id, ApplicationDbContext db, IMemoryCache cache, IServiceProvider sp)
    {
        var district = await db.Districts.FindAsync(id);
        if (district == null) return NotFoundResponse("District not found");
        district.IsActive = true;
        await db.SaveChangesAsync();
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

        if (await db.RoomListings.AnyAsync(l => l.DistrictId == id && l.IsActive))
            return BadRequestResponse("Cannot delete district with active listings");

        await unitOfWork.Districts.DeleteAsync(district);
        await unitOfWork.SaveChangesAsync();

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

        var now = DateTime.UtcNow;
        var totalEarnings = await db.PaymentTransactions
            .Where(t => t.Status == "SUCCESS")
            .SumAsync(t => (int?)t.Amount) ?? 0;
        var currentMonthEarnings = await db.PaymentTransactions
            .Where(t => t.Status == "SUCCESS" && t.CompletedAt.HasValue &&
                        t.CompletedAt.Value.Year == now.Year &&
                        t.CompletedAt.Value.Month == now.Month)
            .SumAsync(t => (int?)t.Amount) ?? 0;

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

    public static async Task<IResult> GetAllFeatures(IUnitOfWork unitOfWork)
    {
        var features = await unitOfWork.Features.GetAllAsync();
        return OkResponse(features.Select(f => new { f.Key, f.DisplayName, f.IsEnabled, f.FreeLimit, f.FreeDays }));
    }

    public static async Task<IResult> GetFeatureByKey(string key, IUnitOfWork unitOfWork)
    {
        var feature = await unitOfWork.Features.GetByKeyAsync(key);
        if (feature == null)
            return NotFoundResponse("Feature not found");
        return OkResponse(new { feature.Key, feature.DisplayName, feature.IsEnabled, feature.FreeLimit, feature.FreeDays });
    }

    public static async Task<IResult> UpdateFeature(string key, PaymentFeatureUpdateRequest request, IUnitOfWork unitOfWork, ApplicationDbContext db)
    {
        var feature = await unitOfWork.Features.GetByKeyAsync(key);
        if (feature == null)
            return NotFoundResponse("Feature not found");

        feature.IsEnabled = request.IsEnabled;
        if (request.FreeLimit.HasValue && request.FreeLimit.Value > 0)
            feature.FreeLimit = request.FreeLimit.Value;
        if (request.FreeDays.HasValue && request.FreeDays.Value > 0)
            feature.FreeDays = request.FreeDays.Value;
        feature.UpdatedAt = DateTime.UtcNow;
        await unitOfWork.Features.UpdateAsync(feature);

        var now = DateTime.UtcNow;
        var yesterday = now.AddDays(-1);

        if (key == FeatureKeys.RoomPayment)
        {
            await db.RoomListings
                .Where(l => l.IsActive && !l.IsDeleted &&
                            !db.RoomMemberships.Any(m => m.UserId == l.UserId && m.IsActive && m.ValidUntil > now))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(l => l.IsActive, false)
                    .SetProperty(l => l.ValidUntil, yesterday)
                    .SetProperty(l => l.UpdatedAt, now));
        }
        else if (key == FeatureKeys.PlotListingPayment)
        {
            await db.PlotListings
                .Where(p => p.IsActive && !p.IsDeleted &&
                            !db.PlotMemberships.Any(m => m.UserId == p.UserId && m.IsActive && m.ValidUntil > now))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.IsActive, false)
                    .SetProperty(p => p.ValidUntil, yesterday)
                    .SetProperty(p => p.UpdatedAt, now));
        }

        return OkResponse(new { feature.Key, feature.IsEnabled, feature.FreeLimit, feature.FreeDays });
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

        var query = db.Users
            .Include(u => u.Memberships.OrderByDescending(m => m.CreatedAt).Take(1))
            .Include(u => u.PlotMemberships.OrderByDescending(m => m.CreatedAt).Take(1))
            .AsQueryable();

        if (isActive.HasValue)
            query = query.Where(u => u.IsActive == isActive.Value);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            query = query.Where(u =>
                u.PhoneNumber.Contains(s) ||
                u.GoogleEmail.ToLower().Contains(s) ||
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
        });

        var result = await projected
            .OrderByDescending(x => x.User.CreatedAt)
            .ToPagedResultAsync(page, pageSize, x =>
            {
                var u = x.User;
                var membership = u.Memberships.OrderByDescending(m => m.CreatedAt).FirstOrDefault();
                var plotMembership = u.PlotMemberships.OrderByDescending(m => m.CreatedAt).FirstOrDefault();

                return new AdminUserDto
                {
                    Id = u.Id,
                    GoogleEmail = u.GoogleEmail,
                    ProfilePhotoUrl = u.ProfilePhotoUrl,
                    PhoneNumber = u.PhoneNumber,
                    IsPhoneVerified = u.IsPhoneVerified,
                    Name = u.Name,
                    IsActive = u.IsActive,
                    HasUsedFreePlan = u.HasUsedFreePlan,
                    CreatedAt = u.CreatedAt,
                    TotalListings = x.TotalListings,
                    ActiveListings = x.ActiveListings,
                    TotalPlotListings = x.TotalPlotListings,
                    ActivePlotListings = x.ActivePlotListings,
                    CurrentMembership = membership == null ? null : new AdminMembershipDto
                    {
                        Id = membership.Id,
                        PlanType = membership.PlanType,
                        ValidFrom = membership.ValidFrom,
                        ValidUntil = membership.ValidUntil,
                        MaxRooms = membership.MaxRooms,
                        IsActive = membership.IsActive,
                    },
                    CurrentPlotMembership = plotMembership == null ? null : new AdminPlotMembershipDto
                    {
                        Id = plotMembership.Id,
                        PlanType = plotMembership.PlanType,
                        ValidFrom = plotMembership.ValidFrom,
                        ValidUntil = plotMembership.ValidUntil,
                        MaxPlotListings = plotMembership.MaxPlotListings,
                        IsActive = plotMembership.IsActive,
                    },
                };
            });

        return OkResponse(result);
    }

    public static async Task<IResult> GetUserById(Guid id, ApplicationDbContext db)
    {
        var u = await db.Users
            .AsNoTracking()
            .Include(u => u.Memberships.OrderByDescending(m => m.CreatedAt).Take(1))
            .Include(u => u.PlotMemberships.OrderByDescending(m => m.CreatedAt).Take(1))
            .FirstOrDefaultAsync(u => u.Id == id);

        if (u == null) return NotFoundResponse("User not found");

        var membership = u.Memberships.OrderByDescending(m => m.CreatedAt).FirstOrDefault();
        var plotMembership = u.PlotMemberships.OrderByDescending(m => m.CreatedAt).FirstOrDefault();

        var dto = new AdminUserDto
        {
            Id = u.Id,
            GoogleEmail = u.GoogleEmail,
            ProfilePhotoUrl = u.ProfilePhotoUrl,
            PhoneNumber = u.PhoneNumber,
            IsPhoneVerified = u.IsPhoneVerified,
            Name = u.Name,
            IsActive = u.IsActive,
            HasUsedFreePlan = u.HasUsedFreePlan,
            CreatedAt = u.CreatedAt,
            TotalListings = await db.RoomListings.CountAsync(l => l.UserId == id && !l.IsDeleted),
            ActiveListings = await db.RoomListings.CountAsync(l => l.UserId == id && !l.IsDeleted && l.IsActive),
            TotalPlotListings = await db.PlotListings.CountAsync(l => l.UserId == id && !l.IsDeleted),
            ActivePlotListings = await db.PlotListings.CountAsync(l => l.UserId == id && !l.IsDeleted && l.IsActive),
            CurrentMembership = membership == null ? null : new AdminMembershipDto
            {
                Id = membership.Id,
                PlanType = membership.PlanType,
                ValidFrom = membership.ValidFrom,
                ValidUntil = membership.ValidUntil,
                MaxRooms = membership.MaxRooms,
                IsActive = membership.IsActive,
            },
            CurrentPlotMembership = plotMembership == null ? null : new AdminPlotMembershipDto
            {
                Id = plotMembership.Id,
                PlanType = plotMembership.PlanType,
                ValidFrom = plotMembership.ValidFrom,
                ValidUntil = plotMembership.ValidUntil,
                MaxPlotListings = plotMembership.MaxPlotListings,
                IsActive = plotMembership.IsActive,
            },
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

    public static async Task<IResult> GetTransactions(
        ApplicationDbContext db,
        int page = 1,
        int pageSize = 20,
        string? status = null)
    {
        pageSize = Math.Clamp(pageSize, 1, 100);
        page = Math.Max(1, page);

        var query = db.PaymentTransactions
            .Include(t => t.User)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(status) && status.ToUpper() != "ALL")
            query = query.Where(t => t.Status == status.ToUpper());

        var result = await query
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => new AdminTransactionDto
            {
                Id = t.Id,
                UserId = t.UserId,
                UserName = t.User != null ? t.User.Name : null,
                UserPhone = t.PhoneNumber,
                PlanType = t.PlanType,
                Amount = t.Amount,
                Currency = t.Currency,
                Status = t.Status,
                RazorpayPaymentId = t.RazorpayPaymentId,
                FailureReason = t.FailureReason,
                CreatedAt = t.CreatedAt,
                CompletedAt = t.CompletedAt,
            })
            .ToPagedResultAsync(page, pageSize);

        return OkResponse(result);
    }

    public static async Task<IResult> ActivateMembership(
        Guid id,
        ActivateMembershipRequest request,
        ApplicationDbContext db)
    {
        var user = await db.Users.FindAsync(id);
        if (user == null) return NotFoundResponse("User not found");

        var planType = request.PlanType.Trim().ToUpperInvariant();
        var plan = await db.RoomPlans.FirstOrDefaultAsync(p => p.PlanType == planType && p.IsEnabled);
        if (plan == null) return BadRequestResponse($"RoomPlan '{planType}' not found or disabled.");

        bool isFree = plan.Price == 0;
        var now = DateTime.UtcNow;

        await db.RoomMemberships
            .Where(m => m.UserId == id && m.IsActive)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.IsActive, false)
                .SetProperty(m => m.UpdatedAt, now));

        var membership = new RoomMembership
        {
            Id = Guid.NewGuid(),
            UserId = id,
            PlanType = planType,
            ValidFrom = now,
            ValidUntil = now.AddDays(plan.Days),
            MaxRooms = plan.RoomLimit,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.RoomMemberships.Add(membership);

        // Mark as used free plan for any price=0 plan
        if (isFree)
            user.HasUsedFreePlan = true;

        user.UpdatedAt = now;
        await db.SaveChangesAsync();

        return OkResponse(new AdminMembershipDto
        {
            Id = membership.Id,
            PlanType = membership.PlanType,
            ValidFrom = membership.ValidFrom,
            ValidUntil = membership.ValidUntil,
            MaxRooms = membership.MaxRooms,
            IsActive = membership.IsActive,
        });
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

    public record CreatePlotPlanRequest(string PlanType, int Price, int Days, int PlotListingLimit, int OriginalPrice = 0, int DiscountPercent = 0);
    public record UpdatePlotPlanRequest(int? Days, int? Price, int? PlotListingLimit, bool? IsEnabled, int? OriginalPrice, int? DiscountPercent);
    public record ActivatePlotMembershipRequest(string PlanType);

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

    public static async Task<IResult> ActivatePlotMembership(
        Guid id, ActivatePlotMembershipRequest request, ApplicationDbContext db)
    {
        var user = await db.Users.FindAsync(id);
        if (user == null) return NotFoundResponse("User not found");

        var planType = request.PlanType.Trim().ToUpperInvariant();
        var plan = await db.PlotPlans.FirstOrDefaultAsync(p => p.PlanType == planType && p.IsEnabled);
        if (plan == null) return BadRequestResponse($"PlotListing plan '{planType}' not found or disabled.");

        var now = DateTime.UtcNow;

        await db.PlotMemberships
            .Where(m => m.UserId == id && m.IsActive)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.IsActive, false)
                .SetProperty(m => m.UpdatedAt, now));

        var membership = new PlotMembership
        {
            Id = Guid.NewGuid(),
            UserId = id,
            PlanType = planType,
            ValidFrom = now,
            ValidUntil = now.AddDays(plan.Days),
            MaxPlotListings = plan.PlotListingLimit,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.PlotMemberships.Add(membership);
        await db.SaveChangesAsync();

        return OkResponse(new
        {
            id = membership.Id,
            planType = membership.PlanType,
            validFrom = membership.ValidFrom,
            validUntil = membership.ValidUntil,
            maxPlotListings = membership.MaxPlotListings,
            isActive = membership.IsActive,
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

    public static async Task<IResult> ToggleAdminListingStatus(
        Guid id, AdminToggleListingRequest request, ApplicationDbContext db)
    {
        var listing = await db.RoomListings.FindAsync(id);
        if (listing == null || listing.IsDeleted) return NotFoundResponse("RoomListing not found");

        listing.IsActive = request.IsActive;
        listing.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

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
}
