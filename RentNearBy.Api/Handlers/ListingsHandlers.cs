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

public static class RoomListingsHandlers
{
    private static readonly TimeSpan ContextCacheTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ReportSubmitWindow = TimeSpan.FromHours(1);
    private const int ReportSubmitMax = 5;

    private static string ContextCacheKey(double lat, double lng)
        => $"context:{lat:F2}:{lng:F2}";

    public static async Task<IResult> GetContext(double lat, double lng, IServiceProvider sp)
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

        var db = sp.GetRequiredService<ApplicationDbContext>();
        var point = new NetTopologySuite.Geometries.Point(lng, lat) { SRID = 4326 };

        var match = await db.Districts
            .AsNoTracking()
            .Include(d => d.Cities.OrderBy(c => c.Name))
            .FirstOrDefaultAsync(d => d.IsActive && d.Boundary != null && d.Boundary.Contains(point));

        if (match == null)
            return NotFoundResponse("No district found for given coordinates");

        var cities = match.Cities.ToList();
        var nearestCity = cities
            .Where(c => c.Latitude.HasValue && c.Longitude.HasValue)
            .Select(c => new { City = c, Dist = Haversine(lat, lng, (double)c.Latitude!, (double)c.Longitude!) })
            .MinBy(x => x.Dist)?.City ?? cities.FirstOrDefault();

        var result = new
        {
            district = new { id = match.Id, name = match.Name, stateName = match.StateName },
            nearestCityId = nearestCity?.Id,
            cities = cities.Select(c => new { id = c.Id, districtId = c.DistrictId, name = c.Name, latitude = c.Latitude, longitude = c.Longitude }).ToList(),
        };

        if (redis != null)
        {
            var json = JsonSerializer.Serialize(result);
            try { await redis.GetDatabase().StringSetAsync(cacheKey, json, ContextCacheTtl); } catch { }
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

    private static string NearbyCacheKey(Guid districtId, double radius, double lat, double lng)
        => $"nearby:{districtId}:{radius:F1}:{lat:F3}:{lng:F3}";

    private static string NearbyDistrictPattern(Guid districtId) => $"nearby:{districtId}:*";

    private static async Task InvalidateNearbyCacheAsync(IConnectionMultiplexer? redis, Guid districtId)
    {
        if (redis == null) return;
        try
        {
            var db = redis.GetDatabase();
            var server = redis.GetServers().FirstOrDefault(s => s.IsConnected);
            if (server == null) return;
            var pattern = NearbyDistrictPattern(districtId);
            await foreach (var key in server.KeysAsync(pattern: pattern))
                await db.KeyDeleteAsync(key);
        }
        catch { /* best-effort: TTL (60s) covers Redis failures */ }
    }

    // Home's "Recently added" feed (HomeHandlers.GetRecentRooms) is district-free and cached under
    // this one fixed key — unlike nearby-cache above, a listing leaving the active/non-deleted set
    // must bust it immediately rather than riding out the cache TTL, since a stale entry here is a
    // tappable card whose detail page 404s, not just a cosmetically-stale count.
    private static async Task InvalidateRecentRoomsCacheAsync(IConnectionMultiplexer? redis)
    {
        if (redis == null) return;
        try { await redis.GetDatabase().KeyDeleteAsync("home:recentRooms"); } catch { }
    }

    public static async Task<IResult> GetNearby(
        double latitude, double longitude, double radius, Guid districtId,
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
        var cacheKey = NearbyCacheKey(districtId, radius, latitude, longitude);

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

        var fetched = (await unitOfWork.RoomListings.GetNearbyAsync(latitude, longitude, radius, districtId)).ToList();

        if (redis != null)
        {
            var json = JsonSerializer.Serialize(fetched);
            try { await redis.GetDatabase().StringSetAsync(cacheKey, json, NearbyCacheTtl, When.NotExists); } catch { }
        }

        if (!isAuth) fetched.ForEach(d => d.OwnerPhone = null);
        return OkResponse(new { items = fetched });
    }

    public static async Task<IResult> GetById(Guid id, IUnitOfWork unitOfWork, ClaimsPrincipal principal)
    {
        var listing = await unitOfWork.RoomListings.GetByIdWithPhotosAsync(id);
        if (listing == null) return NotFoundResponse("RoomListing not found");
        var dto = listing.Adapt<RoomListingDto>();
        if (principal.Identity?.IsAuthenticated != true)
        {
            dto.OwnerPhone = null;
        }
        else if (UsersHandlers.TryGetUserId(principal, out var userId))
        {
            dto.HasReported = await unitOfWork.ListingReports.HasPendingReportFromReporterAsync(id, "Room", userId);
        }
        return OkResponse(dto);
    }

    public static async Task<IResult> GetPlans(IUnitOfWork unitOfWork)
    {
        var plans = await unitOfWork.CoinPlans.GetByFeatureKeyAsync(CoinFeatureKeys.RoomGoLive);
        var result = plans
            .Where(p => p.IsEnabled)
            .OrderBy(p => p.Price)
            .Select(p => new
            {
                planType      = p.PlanType,
                days          = p.Days,
                price         = p.Price,
                originalPrice = p.OriginalPrice,
                discountPercent = p.DiscountPercent,
                roomLimit     = p.Quota,
                isFeatured    = p.IsFeatured,
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

        var (items, hasMore) = await unitOfWork.RoomListings.GetByUserIdPagedAsync(userId, page, pageSize);
        var dtos = items.Select(l => l.Adapt<RoomListingDto>()).ToList();
        var counts = await unitOfWork.ListingReports.GetPendingCountsForListingsAsync(dtos.Select(d => d.Id), "Room");
        foreach (var d in dtos) d.PendingReportCount = counts.GetValueOrDefault(d.Id);
        return OkResponse(new { items = dtos, hasMore });
    }

    public static async Task<IResult> GetListingReports(
        Guid id, ClaimsPrincipal principal, IUnitOfWork unitOfWork, int page = 1, int pageSize = 20)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var listing = await unitOfWork.RoomListings.GetByIdAsync(id);
        if (listing == null) return NotFoundResponse("RoomListing not found");
        if (listing.UserId != userId) return ForbiddenResponse("You do not own this listing");

        pageSize = Math.Clamp(pageSize, 1, 50);
        page = Math.Max(1, page);
        var paged = await unitOfWork.ListingReports.GetPagedForListingAsync(id, "Room", page, pageSize);
        var items = paged.Items.Select(ToListingReportDto).ToList();
        return OkResponse(new { items, hasMore = paged.HasMore });
    }

    private static ListingReportDto ToListingReportDto(ListingReport r) => new()
    {
        Id = r.Id,
        ListingId = r.ListingId,
        ListingType = r.ListingType,
        ReasonName = r.Reason?.Name ?? "",
        Details = r.Details,
        Status = r.Status,
        ResolutionAction = r.ResolutionAction,
        CreatedAt = r.CreatedAt,
        ResolvedAt = r.ResolvedAt,
    };

    public static async Task<IResult> CreateListing(
        CreateListingRequest request,
        ClaimsPrincipal principal,
        IValidator<CreateListingRequest> validator,
        IUnitOfWork unitOfWork,
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

        // Flat, coins-independent cap on how many listings a user can create — separate concern from
        // whether they can afford to go live on one (that's coin-gated, at /go-live time instead).
        var roomLimit = await unitOfWork.ListingLimitSettings.GetByKindAsync(ListingKinds.Room);
        var maxListings = roomLimit?.MaxListings ?? 5;
        var currentCount = await unitOfWork.RoomListings.CountByUserIdAsync(userId);
        if (currentCount >= maxListings)
            return BadRequestResponse($"You can have at most {maxListings} room listing(s). Delete one to add more.");

        var listing = new RoomListing
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
            FurnishedStatus = request.FurnishedStatus,
            IsActive = false, // always — POST /{id}/go-live is now the only path to activation
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await unitOfWork.RoomListings.AddAsync(listing);
        await unitOfWork.SaveChangesAsync();

        // Invalidate cache for the listing's district
        var redis = sp.GetService<IConnectionMultiplexer>();
        await InvalidateNearbyCacheAsync(redis, listing.DistrictId);

        return CreatedResponse(new { listingId = listing.Id }, $"/api/v1/listings/{listing.Id}");
    }

    public static async Task<IResult> UpdateListing(Guid id, UpdateListingRequest request, ClaimsPrincipal principal, IValidator<UpdateListingRequest> validator, IUnitOfWork unitOfWork, IServiceProvider sp)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
            return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var listing = await unitOfWork.RoomListings.GetByIdAsync(id);
        if (listing == null) return NotFoundResponse("RoomListing not found");
        if (listing.UserId != userId) return ForbiddenResponse("You do not own this listing");

        var oldDistrictId = listing.DistrictId;

        if (request.RoomTypeId.HasValue) listing.RoomTypeId = request.RoomTypeId.Value;
        if (request.Description != null) listing.Description = request.Description;
        if (request.PriceMonthly.HasValue) listing.PriceMonthly = request.PriceMonthly.Value;
        if (request.Latitude.HasValue) listing.Latitude = request.Latitude.Value;
        if (request.Longitude.HasValue) listing.Longitude = request.Longitude.Value;
        if (request.Address != null) listing.Address = request.Address;
        if (request.FurnishedStatus != null) listing.FurnishedStatus = request.FurnishedStatus;
        if (request.CityId.HasValue)
        {
            var city = await unitOfWork.Cities.GetByIdAsync(request.CityId.Value);
            if (city == null) return BadRequestResponse("Selected city does not exist");
            listing.CityId = request.CityId.Value;
            listing.DistrictId = city.DistrictId;
        }
        if (request.IsActive.HasValue)
        {
            if (request.IsActive.Value)
                return BadRequestResponse("Use POST /listings/{id}/go-live to activate a listing.");
            listing.IsActive = false; // deactivating is always free — ValidUntil is untouched, so a
                                       // later Go-Live within the same window reactivates for free too
        }
        listing.UpdatedAt = DateTime.UtcNow;

        await unitOfWork.RoomListings.UpdateAsync(listing);
        await unitOfWork.SaveChangesAsync();

        if (request.IsActive.HasValue && request.IsActive.Value == false)
            await unitOfWork.ListingReports.AutoResolvePendingForListingAsync(id, "Room");

        var redis = sp.GetService<IConnectionMultiplexer>();
        await InvalidateNearbyCacheAsync(redis, listing.DistrictId);
        if (oldDistrictId != listing.DistrictId)
            await InvalidateNearbyCacheAsync(redis, oldDistrictId);
        if (request.IsActive.HasValue && request.IsActive.Value == false)
            await InvalidateRecentRoomsCacheAsync(redis);

        return OkResponse(new { success = true });
    }

    public static async Task<IResult> DeleteListing(Guid id, ClaimsPrincipal principal, IUnitOfWork unitOfWork, IPhotoService photoService, IServiceProvider sp)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var listing = await unitOfWork.RoomListings.GetByIdAsync(id);
        if (listing == null) return NotFoundResponse("RoomListing not found");
        if (listing.UserId != userId) return ForbiddenResponse("You do not own this listing");

        var districtId = listing.DistrictId;

        await photoService.DeleteRoomPhotosAsync(userId, id);

        listing.IsDeleted = true;
        listing.DeletedAt = DateTime.UtcNow;
        await unitOfWork.RoomListings.UpdateAsync(listing);
        await unitOfWork.SaveChangesAsync();

        await unitOfWork.ListingReports.AutoResolvePendingForListingAsync(id, "Room");

        var redis = sp.GetService<IConnectionMultiplexer>();
        await InvalidateNearbyCacheAsync(redis, districtId);
        await InvalidateRecentRoomsCacheAsync(redis);

        return NoContentResponse();
    }

    public static async Task<IResult> ReportListing(
        Guid id, CreateListingReportRequest request,
        ClaimsPrincipal principal,
        IValidator<CreateListingReportRequest> validator,
        IUnitOfWork unitOfWork, IRabbitMqPublisher publisher,
        IRateLimitService rateLimiter, HttpContext httpContext)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var reportRl = await rateLimiter.CheckAsync($"report:submit:{userId}", ReportSubmitMax, ReportSubmitWindow);
        if (!reportRl.IsAllowed)
        {
            httpContext.Response.Headers["Retry-After"] = ((int)reportRl.RetryAfter!.Value.TotalSeconds).ToString();
            return TooManyRequestsResponse();
        }

        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var listing = await unitOfWork.RoomListings.GetByIdAsync(id);
        if (listing == null) return NotFoundResponse("RoomListing not found");
        if (listing.UserId == userId) return BadRequestResponse("You cannot report your own listing");

        var reason = await unitOfWork.ReportReasons.GetByIdAsync(request.ReasonId);
        if (reason == null) return BadRequestResponse("Invalid reason");

        var reporter = await unitOfWork.Users.GetByIdAsync(userId);
        var owner = await unitOfWork.Users.GetByIdAsync(listing.UserId);

        var isFirstPending = !await unitOfWork.ListingReports.HasPendingReportForListingAsync(id, "Room");

        var report = new ListingReport
        {
            Id = Guid.NewGuid(),
            ListingId = id,
            ListingType = "Room",
            ReporterUserId = userId,
            ReporterName = reporter?.Name ?? "Unknown",
            ReporterMobile = reporter?.PhoneNumber ?? "",
            ReportedUserId = listing.UserId,
            ReportedName = owner?.Name ?? "Unknown",
            ReportedMobile = owner?.PhoneNumber ?? "",
            ReasonId = request.ReasonId,
            Details = request.Details,
            Status = "Pending",
            CreatedAt = DateTime.UtcNow,
        };

        await unitOfWork.ListingReports.AddAsync(report);
        try
        {
            await unitOfWork.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            return ConflictResponse("You have already reported this listing");
        }

        var message = new ReportFiledMessage
        {
            OwnerId = listing.UserId,
            ListingId = listing.Id,
            ListingType = "Room",
            ReasonName = reason.Name,
            ListingTitle = listing.Address ?? "your listing",
            NotifyOwner = isFirstPending,
        };
        try { await publisher.PublishAsync("report.filed", JsonSerializer.Serialize(message)); }
        catch (Exception) { /* best-effort — report row is already saved regardless of push delivery */ }

        return CreatedResponse(new { reportId = report.Id }, $"/api/v1/admin/reports/{report.Id}");
    }

    public static async Task<IResult> UploadPhoto(Guid id, IFormFile photo, ClaimsPrincipal principal, IUnitOfWork unitOfWork, IPhotoService photoService, IServiceProvider sp)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var listing = await unitOfWork.RoomListings.GetByIdWithPhotosAsync(id);
        if (listing == null) return NotFoundResponse("RoomListing not found");
        if (listing.UserId != userId) return ForbiddenResponse("You do not own this listing");
        if (listing.Photos.Count >= 5) return BadRequestResponse("Maximum 5 photos allowed per listing");
        if (photo.Length > 10 * 1024 * 1024) return BadRequestResponse("Photo size must not exceed 10MB");

        using var stream = photo.OpenReadStream();
        if (!IsValidImageMagicBytes(stream)) return BadRequestResponse("File must be a valid image (JPEG, PNG or WebP)");
        stream.Position = 0;
        var (url, filePath) = await photoService.SavePhotoAsync(stream, photo.FileName, userId, id);

        var listingPhoto = new RoomPhoto
        {
            Id = Guid.NewGuid(),
            RoomListingId = id,
            PhotoUrl = url,
            FilePath = filePath,
            PhotoOrder = listing.Photos.Count,
            UploadedAt = DateTime.UtcNow
        };

        await unitOfWork.RoomListings.AddPhotoAsync(listingPhoto);
        await unitOfWork.SaveChangesAsync();

        // Invalidate cache after photo upload so thumbnail appears immediately
        var redis = sp.GetService<IConnectionMultiplexer>();
        await InvalidateNearbyCacheAsync(redis, listing.DistrictId);

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

        var listing = await unitOfWork.RoomListings.GetByIdWithPhotosAsync(id);
        if (listing == null) return NotFoundResponse("RoomListing not found");
        if (listing.UserId != userId) return ForbiddenResponse("You do not own this listing");

        var photo = listing.Photos.FirstOrDefault(p => p.Id == photoId);
        if (photo == null) return NotFoundResponse("Photo not found");

        await photoService.DeletePhotoAsync(photo.FilePath);
        unitOfWork.RoomListings.RemovePhoto(photo);
        await unitOfWork.SaveChangesAsync();

        // Invalidate cache after photo deletion so thumbnail updates
        var redis = sp.GetService<IConnectionMultiplexer>();
        await InvalidateNearbyCacheAsync(redis, listing.DistrictId);

        return NoContentResponse();
    }
}
