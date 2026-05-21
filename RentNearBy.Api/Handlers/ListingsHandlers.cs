using System.Security.Claims;
using System.Text.Json;
using FluentValidation;
using Mapster;
using Microsoft.EntityFrameworkCore;
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

public static class ListingsHandlers
{
    private static readonly TimeSpan ContextCacheTtl = TimeSpan.FromMinutes(10);

    private static string ContextCacheKey(double lat, double lng)
        => $"context:{lat:F2}:{lng:F2}";

    public static async Task<IResult> GetContext(double lat, double lng, IUnitOfWork unitOfWork, IServiceProvider sp)
    {
        if (lat < -90 || lat > 90 || lng < -180 || lng > 180)
            return BadRequestResponse("Invalid coordinates");

        var redis = sp.GetService<IConnectionMultiplexer>();
        var cacheKey = ContextCacheKey(lat, lng);

        if (redis != null)
        {
            RedisValue cached = default;
            try { cached = await redis.GetDatabase().StringGetAsync(cacheKey); } catch { }
            if (cached.HasValue)
            {
                try
                {
                    var element = JsonSerializer.Deserialize<JsonElement>(cached!);
                    return OkResponse(element);
                }
                catch (JsonException) { /* corrupted cache entry — fall through to DB */ }
            }
        }

        var districts = (await unitOfWork.Districts.GetAllWithCitiesAsync()).ToList();
        if (districts.Count == 0) return BadRequestResponse("No districts configured");

        var nearestDistrict = districts
            .Select(d => new { District = d, Dist = Haversine(lat, lng, (double)(d.Latitude ?? 0), (double)(d.Longitude ?? 0)) })
            .MinBy(x => x.Dist)!.District;

        var cities = nearestDistrict.Cities.ToList();

        var nearestCity = cities
            .Where(c => c.Latitude.HasValue && c.Longitude.HasValue)
            .Select(c => new { City = c, Dist = Haversine(lat, lng, (double)c.Latitude!, (double)c.Longitude!) })
            .MinBy(x => x.Dist)?.City ?? cities.FirstOrDefault();

        var result = new
        {
            district = new { id = nearestDistrict.Id, name = nearestDistrict.Name, latitude = nearestDistrict.Latitude, longitude = nearestDistrict.Longitude },
            nearestCityId = nearestCity?.Id,
            cities = cities.Select(c => new { id = c.Id, districtId = c.DistrictId, name = c.Name, latitude = c.Latitude, longitude = c.Longitude }).ToList(),
        };

        if (redis != null)
        {
            var json = JsonSerializer.Serialize(result);
            try { await redis.GetDatabase().StringSetAsync(cacheKey, json, ContextCacheTtl, When.NotExists); } catch { }
        }

        return OkResponse(result);
    }

    private static double Haversine(double lat1, double lng1, double lat2, double lng2)
    {
        const double R = 6371.0;
        var dlat = (lat2 - lat1) * Math.PI / 180;
        var dlng = (lng2 - lng1) * Math.PI / 180;
        var a = Math.Sin(dlat / 2) * Math.Sin(dlat / 2) +
                Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) *
                Math.Sin(dlng / 2) * Math.Sin(dlng / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static readonly TimeSpan NearbyCacheTtl = TimeSpan.FromSeconds(60);

    private static string NearbyCacheKey(Guid cityId, double radius, double lat, double lng)
        => $"nearby:{cityId}:{radius:F1}:{lat:F3}:{lng:F3}";

    private static string NearbyCityPattern(Guid cityId) => $"nearby:{cityId}:*";

    private static async Task InvalidateNearbyCacheAsync(IConnectionMultiplexer? redis, Guid cityId)
    {
        if (redis == null) return;
        try
        {
            var db = redis.GetDatabase();
            var server = redis.GetServers().FirstOrDefault(s => s.IsConnected);
            if (server == null) return;
            var pattern = NearbyCityPattern(cityId);
            await foreach (var key in server.KeysAsync(pattern: pattern))
                await db.KeyDeleteAsync(key);
        }
        catch { /* best-effort: TTL (60s) covers Redis failures */ }
    }

    public static async Task<IResult> GetNearby(
        double latitude, double longitude, double radius, Guid cityId,
        IUnitOfWork unitOfWork,
        ClaimsPrincipal principal,
        IServiceProvider sp)
    {
        if (radius < 1 || radius > 50)
            return BadRequestResponse("Radius must be between 1 and 50 km");
        if (latitude < -90 || latitude > 90 || longitude < -180 || longitude > 180)
            return BadRequestResponse("Invalid coordinates");

        var redis = sp.GetService<IConnectionMultiplexer>();
        var isAuth = principal.Identity?.IsAuthenticated == true;
        var cacheKey = NearbyCacheKey(cityId, radius, latitude, longitude);

        if (redis != null)
        {
            var db = redis.GetDatabase();
            RedisValue cached = default;
            try { cached = await db.StringGetAsync(cacheKey); } catch { }
            if (cached.HasValue)
            {
                try
                {
                    var items = JsonSerializer.Deserialize<List<NearbyListingDto>>(cached!);
                    if (items != null)
                    {
                        if (!isAuth) items.ForEach(d => d.OwnerPhone = null);
                        return OkResponse(new { items });
                    }
                }
                catch (JsonException) { /* corrupted cache entry — fall through to DB */ }
            }
        }

        var fetched = (await unitOfWork.Listings.GetNearbyAsync(latitude, longitude, radius, cityId)).ToList();

        if (redis != null)
        {
            var json = JsonSerializer.Serialize(fetched);
            try { await redis.GetDatabase().StringSetAsync(cacheKey, json, NearbyCacheTtl, When.NotExists); } catch { }
        }

        if (!isAuth) fetched.ForEach(d => d.OwnerPhone = null);
        return OkResponse(new { items = fetched });
    }

    public static async Task<IResult> Search(Guid? districtId, Guid? roomTypeId, int? priceMin, int? priceMax, IUnitOfWork unitOfWork)
    {
        var listings = await unitOfWork.Listings.SearchAsync(districtId, roomTypeId, priceMin, priceMax);
        return OkResponse(listings.Select(l => l.Adapt<ListingDto>()).ToList());
    }

    public static async Task<IResult> GetById(Guid id, IUnitOfWork unitOfWork, ClaimsPrincipal principal)
    {
        var listing = await unitOfWork.Listings.GetByIdWithPhotosAsync(id);
        if (listing == null) return NotFoundResponse("Listing not found");
        var dto = listing.Adapt<ListingDto>();
        if (principal.Identity?.IsAuthenticated != true) dto.OwnerPhone = null;
        return OkResponse(dto);
    }

    public static async Task<IResult> GetPlans(IUnitOfWork unitOfWork)
    {
        var plans = await unitOfWork.Plans.GetAllAsync();
        var result = plans
            .Where(p => p.IsEnabled)
            .OrderBy(p => p.Price)
            .Select(p => new
            {
                planType  = p.PlanType,
                days      = p.Days,
                price     = p.Price,
                roomLimit = p.RoomLimit,
            })
            .ToList();
        return OkResponse(result);
    }

    public static async Task<IResult> GetMyListings(
        ClaimsPrincipal principal, IUnitOfWork unitOfWork, int page = 1, int pageSize = 10)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();
        if (pageSize < 1 || pageSize > 50) pageSize = 10;
        if (page < 1) page = 1;

        var (items, hasMore) = await unitOfWork.Listings.GetByUserIdPagedAsync(userId, page, pageSize);
        return OkResponse(new { items = items.Select(l => l.Adapt<ListingDto>()).ToList(), hasMore });
    }

    public static async Task<IResult> CreateListing(
        CreateListingRequest request,
        ClaimsPrincipal principal,
        IValidator<CreateListingRequest> validator,
        IUnitOfWork unitOfWork,
        IPaymentService paymentService,
        IServiceProvider sp)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
            return BadRequestResponse(validation.Errors[0].ErrorMessage);

        if (request.CityId.HasValue)
        {
            var city = await unitOfWork.Cities.GetByIdAsync(request.CityId.Value);
            if (city == null) return BadRequestResponse("Selected city does not exist");
            if (city.DistrictId != request.DistrictId)
                return BadRequestResponse("Selected city does not belong to the selected district");
        }

        var roomFeature = await unitOfWork.Features.GetByKeyAsync(FeatureKeys.RoomPayment);
        if (roomFeature == null || !roomFeature.IsEnabled)
        {
            var freeLimit = roomFeature?.FreeLimit ?? 1;
            var db = sp.GetRequiredService<ApplicationDbContext>();
            var totalCount = await db.Listings
                .Where(l => l.UserId == userId && !l.IsDeleted)
                .CountAsync();
            if (totalCount >= freeLimit)
                return BadRequestResponse($"Free mode limit: you can have at most {freeLimit} listing(s). Delete one to add more.");
        }
        else
        {
            var membership = await unitOfWork.UserMemberships.GetActiveByUserIdAsync(userId);
            if (membership != null && membership.IsActive)
            {
                var db = sp.GetRequiredService<ApplicationDbContext>();
                var totalCount = await db.Listings
                    .Where(l => l.UserId == userId && !l.IsDeleted)
                    .CountAsync();
                if (totalCount >= membership.MaxRooms)
                    return BadRequestResponse($"You have reached your plan limit of {membership.MaxRooms} listing(s). Upgrade your plan to add more.");
            }
        }

        var listing = new Listing
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            RoomTypeId = request.RoomTypeId,
            Description = request.Description,
            PriceMonthly = request.PriceMonthly,
            Latitude = request.Latitude,
            Longitude = request.Longitude,
            Address = request.Address,
            DistrictId = request.DistrictId,
            CityId = request.CityId,
            IsActive = false,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        // Auto-activate for paid members with available capacity — routing by plan price, not plan name
        var activeMembership = await unitOfWork.UserMemberships.GetActiveByUserIdAsync(userId);
        if (activeMembership != null && activeMembership.IsActive)
        {
            var activePlan = await unitOfWork.Plans.GetByPlanTypeAsync(activeMembership.PlanType);
            if (activePlan?.Price > 0)
            {
                var canActivate = await paymentService.CanUserActivateListingAsync(userId);
                if (canActivate)
                {
                    listing.IsActive = true;
                    listing.ValidUntil = activeMembership.ValidUntil;
                }
            }
        }

        await unitOfWork.Listings.AddAsync(listing);
        await unitOfWork.SaveChangesAsync();

        // Invalidate cache if city is specified
        var redis = sp.GetService<IConnectionMultiplexer>();
        if (request.CityId.HasValue)
            await InvalidateNearbyCacheAsync(redis, request.CityId.Value);
        else if (listing.DistrictId != Guid.Empty)
        {
            // If no city but district exists, invalidate all cities in district
            var district = await unitOfWork.Districts.GetByIdAsync(listing.DistrictId);
            if (district?.Cities != null)
            {
                foreach (var city in district.Cities)
                    await InvalidateNearbyCacheAsync(redis, city.Id);
            }
        }

        return CreatedResponse(new { listingId = listing.Id }, $"/api/v1/listings/{listing.Id}");
    }

    public static async Task<IResult> UpdateListing(Guid id, UpdateListingRequest request, ClaimsPrincipal principal, IValidator<UpdateListingRequest> validator, IUnitOfWork unitOfWork, IServiceProvider sp)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
            return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var listing = await unitOfWork.Listings.GetByIdAsync(id);
        if (listing == null) return NotFoundResponse("Listing not found");
        if (listing.UserId != userId) return ForbiddenResponse("You do not own this listing");

        var oldCityId = listing.CityId;

        if (request.RoomTypeId.HasValue) listing.RoomTypeId = request.RoomTypeId.Value;
        if (request.Description != null) listing.Description = request.Description;
        if (request.PriceMonthly.HasValue) listing.PriceMonthly = request.PriceMonthly.Value;
        if (request.Latitude.HasValue) listing.Latitude = request.Latitude.Value;
        if (request.Longitude.HasValue) listing.Longitude = request.Longitude.Value;
        if (request.Address != null) listing.Address = request.Address;
        if (request.CityId.HasValue)
        {
            var city = await unitOfWork.Cities.GetByIdAsync(request.CityId.Value);
            if (city == null) return BadRequestResponse("Selected city does not exist");
            listing.CityId = request.CityId.Value;
            listing.DistrictId = city.DistrictId;
        }
        if (request.IsActive.HasValue)
        {
            if (request.IsActive.Value == true)
            {
                var roomFeature = await unitOfWork.Features.GetByKeyAsync(FeatureKeys.RoomPayment);
                if (roomFeature == null || !roomFeature.IsEnabled)
                {
                    var freeLimit = roomFeature?.FreeLimit ?? 1;
                    var freeDays = roomFeature?.FreeDays ?? 2;
                    var db = sp.GetRequiredService<ApplicationDbContext>();
                    var totalCount = await db.Listings
                        .Where(l => l.UserId == userId && !l.IsDeleted && l.Id != id)
                        .CountAsync();
                    if (totalCount >= freeLimit)
                        return BadRequestResponse($"Free mode limit: you can have at most {freeLimit} listing(s).");
                    listing.ValidUntil = DateTime.UtcNow.AddDays(freeDays);
                }
                else
                {
                    var membership = await unitOfWork.UserMemberships.GetActiveByUserIdAsync(userId);
                    if (membership == null || !membership.IsActive)
                        return BadRequestResponse("You need an active plan to go live. Please purchase a plan.");
                    var svc = sp.GetRequiredService<IPaymentService>();
                    var canActivate = await svc.CanUserActivateListingAsync(userId);
                    if (!canActivate)
                        return BadRequestResponse($"You have reached your active listing limit of {membership.MaxRooms}. Upgrade your plan.");
                }
            }
            listing.IsActive = request.IsActive.Value;
        }
        listing.UpdatedAt = DateTime.UtcNow;

        await unitOfWork.Listings.UpdateAsync(listing);
        await unitOfWork.SaveChangesAsync();

        var redis = sp.GetService<IConnectionMultiplexer>();
        var newCityId = listing.CityId;
        if (newCityId.HasValue)
            await InvalidateNearbyCacheAsync(redis, newCityId.Value);
        if (oldCityId.HasValue && oldCityId != newCityId)
            await InvalidateNearbyCacheAsync(redis, oldCityId.Value);

        return OkResponse(new { success = true });
    }

    public static async Task<IResult> DeleteListing(Guid id, ClaimsPrincipal principal, IUnitOfWork unitOfWork, IPhotoService photoService, IServiceProvider sp)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var listing = await unitOfWork.Listings.GetByIdAsync(id);
        if (listing == null) return NotFoundResponse("Listing not found");
        if (listing.UserId != userId) return ForbiddenResponse("You do not own this listing");

        var cityId = listing.CityId;

        await photoService.DeleteListingPhotosAsync(userId, id);

        listing.IsDeleted = true;
        listing.DeletedAt = DateTime.UtcNow;
        await unitOfWork.Listings.UpdateAsync(listing);
        await unitOfWork.SaveChangesAsync();

        if (cityId.HasValue)
            await InvalidateNearbyCacheAsync(sp.GetService<IConnectionMultiplexer>(), cityId.Value);

        return NoContentResponse();
    }

    public static async Task<IResult> UploadPhoto(Guid id, IFormFile photo, ClaimsPrincipal principal, IUnitOfWork unitOfWork, IPhotoService photoService, IServiceProvider sp)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var listing = await unitOfWork.Listings.GetByIdWithPhotosAsync(id);
        if (listing == null) return NotFoundResponse("Listing not found");
        if (listing.UserId != userId) return ForbiddenResponse("You do not own this listing");
        if (listing.Photos.Count >= 5) return BadRequestResponse("Maximum 5 photos allowed per listing");
        if (photo.Length > 10 * 1024 * 1024) return BadRequestResponse("Photo size must not exceed 10MB");

        using var stream = photo.OpenReadStream();
        if (!IsValidImageMagicBytes(stream)) return BadRequestResponse("File must be a valid image (JPEG, PNG or WebP)");
        stream.Position = 0;
        var (url, filePath) = await photoService.SavePhotoAsync(stream, photo.FileName, userId, id);

        var listingPhoto = new ListingPhoto
        {
            Id = Guid.NewGuid(),
            ListingId = id,
            PhotoUrl = url,
            FilePath = filePath,
            PhotoOrder = listing.Photos.Count,
            UploadedAt = DateTime.UtcNow
        };

        await unitOfWork.Listings.AddPhotoAsync(listingPhoto);
        await unitOfWork.SaveChangesAsync();

        // Invalidate cache after photo upload so thumbnail appears immediately
        var redis = sp.GetService<IConnectionMultiplexer>();
        if (listing.CityId.HasValue)
            await InvalidateNearbyCacheAsync(redis, listing.CityId.Value);

        return CreatedResponse(new { photoUrl = url, photoId = listingPhoto.Id }, url);
    }

    private static bool IsValidImageMagicBytes(Stream stream)
    {
        var header = new byte[12];
        var read = stream.Read(header, 0, header.Length);
        if (read < 3) return false;
        if (header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF) return true;
        if (read >= 8 && header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47) return true;
        if (read >= 12 && header[0] == 0x52 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x46
            && header[8] == 0x57 && header[9] == 0x45 && header[10] == 0x42 && header[11] == 0x50) return true;
        return false;
    }

    public static async Task<IResult> DeletePhoto(Guid id, Guid photoId, ClaimsPrincipal principal, IUnitOfWork unitOfWork, IPhotoService photoService, IServiceProvider sp)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var listing = await unitOfWork.Listings.GetByIdWithPhotosAsync(id);
        if (listing == null) return NotFoundResponse("Listing not found");
        if (listing.UserId != userId) return ForbiddenResponse("You do not own this listing");

        var photo = listing.Photos.FirstOrDefault(p => p.Id == photoId);
        if (photo == null) return NotFoundResponse("Photo not found");

        await photoService.DeletePhotoAsync(photo.FilePath);
        unitOfWork.Listings.RemovePhoto(photo);
        await unitOfWork.SaveChangesAsync();

        // Invalidate cache after photo deletion so thumbnail updates
        var redis = sp.GetService<IConnectionMultiplexer>();
        if (listing.CityId.HasValue)
            await InvalidateNearbyCacheAsync(redis, listing.CityId.Value);

        return NoContentResponse();
    }
}
