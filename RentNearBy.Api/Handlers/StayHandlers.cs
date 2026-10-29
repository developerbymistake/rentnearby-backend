using System.Globalization;
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
using RentNearBy.Core.Utils;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services;
using StackExchange.Redis;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

// Redis keys for the stay feeds. Busted on every change that moves a stay in or out of the public
// set (approve, reject, accepting toggle, price edit, delete, report takedown, role purge).
public static class StayCache
{
    public static readonly TimeSpan BrowseTtl = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan NearbyTtl = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan NearestTtl = TimeSpan.FromSeconds(60);

    public static string BrowseKey(Guid districtId, Guid? cityId, string? meals, Guid? stayTypeId, string sort, int pageSize)
        => $"stays:browse:{districtId:N}:{cityId:N}:{meals}:{stayTypeId:N}:{sort}:{pageSize}";

    public static string NearbyKey(Guid districtId, Guid? stayTypeId, double radius, double lat, double lng)
        => $"stays:nearby:{districtId}:{stayTypeId:N}:{radius:F1}:{lat:F3}:{lng:F3}";

    public static string NearestKey(Guid districtId, Guid? stayTypeId, int count, double lat, double lng)
        => $"stays:nearest:{districtId}:{stayTypeId:N}:{count}:{lat:F3}:{lng:F3}";

    public static async Task InvalidateAsync(IConnectionMultiplexer? redis, Guid? districtId)
    {
        if (redis == null || districtId == null) return;
        try
        {
            var db = redis.GetDatabase();
            var server = redis.GetServers().FirstOrDefault(s => s.IsConnected);
            if (server == null) return;
            var id = districtId.Value;
            foreach (var pattern in new[] { $"stays:browse:{id:N}:*", $"stays:nearby:{id}:*", $"stays:nearest:{id}:*" })
                await foreach (var key in server.KeysAsync(pattern: pattern))
                    await db.KeyDeleteAsync(key);
        }
        catch { /* best-effort: TTL (<= 60s) covers Redis failures */ }
    }
}

public static class StayHandlers
{
    private static readonly TimeSpan ReportSubmitWindow = TimeSpan.FromHours(1);
    private const int ReportSubmitMax = 5;
    private static readonly TimeSpan NearestRateLimitWindow = TimeSpan.FromMinutes(10);
    private const int NearestRateLimitMax = 20;
    private const int DefaultPageSize = 10;
    private const int MaxPageSize = 30;
    private const string StayNotFound = "Stay not found";

    private static readonly HashSet<string> SortValues = new() { "newest", "price_asc", "price_desc" };

    private static bool IsPublic(StayListing s) => !s.IsDeleted && s.Status == StayStatuses.Approved;

    private static bool IsEditable(StayListing s) => s.Status is StayStatuses.Draft or StayStatuses.Rejected;

    private static IResult LockedResponse(StayListing s) => s.Status == StayStatuses.PendingReview
        ? ConflictResponse("Your stay is under review.", StayErrorTypes.StayLocked)
        : ConflictResponse("A live stay can only change prices and Accepting guests.", StayErrorTypes.StayLocked);

    private static IResult StepLocked(int step) =>
        ConflictResponse($"Complete step {step} first.", StayErrorTypes.StayStepLocked);

    private static IResult Concurrent() =>
        ConflictResponse("This stay was just modified by another request. Please retry.", "CONCURRENT_UPDATE");


    private static async Task<StayOwnerDto> ToOwnerDtoAsync(StayListing stay, IUnitOfWork unitOfWork)
    {
        var dto = stay.Adapt<StayOwnerDto>();
        dto.Step1Done = StaySetupRules.Step1Done(stay);
        dto.RoomsDone = StaySetupRules.RoomsDone(stay);
        dto.RulesDone = StaySetupRules.RulesDone(stay);
        dto.Submitted = StaySetupRules.Submitted(stay);
        dto.Percent = StaySetupRules.Percent(stay);
        var counts = await unitOfWork.ListingReports.GetPendingCountsForListingsAsync(new[] { stay.Id }, ListingKinds.Stay);
        dto.PendingReportCount = counts.GetValueOrDefault(stay.Id);
        return dto;
    }

    private static void RecomputeMinPrice(StayListing stay) =>
        stay.MinPricePerNight = stay.Rooms.Count == 0 ? null : stay.Rooms.Min(r => r.PricePerNight);

    // ---- Public ----

    public static async Task<IResult> GetBrowse(
        Guid districtId, Guid? cityId, string? meals, Guid? stayTypeId, string? sortBy, string? cursor,
        IUnitOfWork unitOfWork, IServiceProvider sp, int page = 1, int pageSize = DefaultPageSize)
    {
        var sort = SortValues.Contains(sortBy ?? "") ? sortBy! : "newest";
        var mealsFilter = meals != null && StayMeals.All.Contains(meals) ? meals : null;
        var size = Math.Clamp(pageSize <= 0 ? DefaultPageSize : pageSize, 1, MaxPageSize);
        var pageNo = Math.Max(page, 1);
        var firstPage = string.IsNullOrEmpty(cursor) && pageNo <= 1;
        var redis = firstPage ? sp.GetService<IConnectionMultiplexer>() : null;
        var cacheKey = StayCache.BrowseKey(districtId, cityId, mealsFilter, stayTypeId, sort, size);

        BrowsePage<StayBrowseDto>? result = null;
        if (redis != null)
        {
            try
            {
                var cached = await redis.GetDatabase().StringGetAsync(cacheKey);
                if (cached.HasValue) result = JsonSerializer.Deserialize<BrowsePage<StayBrowseDto>>(cached!);
            }
            catch { result = null; }
        }

        if (result == null)
        {
            result = await unitOfWork.Stays.BrowsePagedAsync(districtId, cityId, mealsFilter, stayTypeId, sort, pageNo, size, cursor);
            if (redis != null)
            {
                try { await redis.GetDatabase().StringSetAsync(cacheKey, JsonSerializer.Serialize(result), StayCache.BrowseTtl); } catch { }
            }
        }

        return OkResponse(new { items = result.Items, hasMore = result.HasMore, nextCursor = result.NextCursor });
    }

    // Anonymous callers get neither OwnerName nor OwnerPhone. The cache holds full rows, so the
    // redaction runs per request after the read.
    private static void RedactForAnonymous(List<StayNearbyDto> items)
    {
        foreach (var d in items) { d.OwnerName = null; d.OwnerPhone = null; }
    }

    private static async Task<List<StayNearbyDto>?> ReadNearbyCacheAsync(IConnectionMultiplexer? redis, string key)
    {
        if (redis == null) return null;
        RedisValue cached = default;
        try { cached = await redis.GetDatabase().StringGetAsync(key); } catch { }
        if (!cached.HasValue) return null;
        try { return JsonSerializer.Deserialize<List<StayNearbyDto>>(cached!); }
        catch (JsonException) { return null; }
    }

    private static async Task WriteNearbyCacheAsync(IConnectionMultiplexer? redis, string key, List<StayNearbyDto> items, TimeSpan ttl)
    {
        if (redis == null) return;
        try { await redis.GetDatabase().StringSetAsync(key, JsonSerializer.Serialize(items), ttl, When.NotExists); } catch { }
    }

    public static async Task<IResult> GetNearby(
        double latitude, double longitude, double radius, Guid districtId, Guid? stayTypeId,
        IUnitOfWork unitOfWork, ClaimsPrincipal principal, IServiceProvider sp)
    {
        if (radius < 1 || radius > 50)
            return BadRequestResponse("Radius must be between 1 and 50 km");
        if (latitude < -90 || latitude > 90 || longitude < -180 || longitude > 180)
            return BadRequestResponse("Invalid coordinates");

        var redis = sp.GetService<IConnectionMultiplexer>();
        var isAuth = principal.Identity?.IsAuthenticated == true;
        var cacheKey = StayCache.NearbyKey(districtId, stayTypeId, radius, latitude, longitude);

        var items = await ReadNearbyCacheAsync(redis, cacheKey);
        if (items == null)
        {
            items = (await unitOfWork.Stays.GetNearbyAsync(latitude, longitude, radius, districtId, stayTypeId)).ToList();
            await WriteNearbyCacheAsync(redis, cacheKey, items, StayCache.NearbyTtl);
        }

        if (!isAuth) RedactForAnonymous(items);
        return OkResponse(new { items });
    }

    public static async Task<IResult> GetNearest(
        double latitude, double longitude, Guid districtId, Guid? stayTypeId,
        IUnitOfWork unitOfWork, ClaimsPrincipal principal, IServiceProvider sp,
        IRateLimitService rateLimiter, HttpContext httpContext, int count = 5)
    {
        if (count < 1 || count > 10)
            return BadRequestResponse("Count must be between 1 and 10");
        if (latitude < -90 || latitude > 90 || longitude < -180 || longitude > 180)
            return BadRequestResponse("Invalid coordinates");

        var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var rl = await rateLimiter.CheckAsync($"stays:nearest:ip:{ip}", NearestRateLimitMax, NearestRateLimitWindow);
        if (!rl.IsAllowed)
        {
            httpContext.Response.Headers["Retry-After"] = ((int)rl.RetryAfter!.Value.TotalSeconds).ToString();
            return TooManyRequestsResponse();
        }

        var redis = sp.GetService<IConnectionMultiplexer>();
        var isAuth = principal.Identity?.IsAuthenticated == true;
        var cacheKey = StayCache.NearestKey(districtId, stayTypeId, count, latitude, longitude);

        var items = await ReadNearbyCacheAsync(redis, cacheKey);
        if (items == null)
        {
            items = (await unitOfWork.Stays.GetNearestAsync(latitude, longitude, count, districtId, stayTypeId)).ToList();
            await WriteNearbyCacheAsync(redis, cacheKey, items, StayCache.NearestTtl);
        }

        if (!isAuth) RedactForAnonymous(items);
        return OkResponse(new { items });
    }

    // Share-link resolver. StayPublicSlugDto carries no owner fields, and the repository only
    // returns Approved, non-deleted stays, so unreviewed content never leaks through a slug.
    public static async Task<IResult> GetBySlug(string slug, IUnitOfWork unitOfWork)
    {
        var stay = await unitOfWork.Stays.GetBySlugWithAllAsync(slug);
        if (stay == null) return NotFoundResponse(StayNotFound);
        return OkResponse(stay.Adapt<StayPublicSlugDto>());
    }

    public static async Task<IResult> GetById(Guid id, IUnitOfWork unitOfWork, ClaimsPrincipal principal)
    {
        var stay = await unitOfWork.Stays.GetByIdWithAllAsync(id);
        if (stay == null) return NotFoundResponse(StayNotFound);

        var isAuth = principal.Identity?.IsAuthenticated == true;
        var hasUser = UsersHandlers.TryGetUserId(principal, out var userId);
        var isOwner = isAuth && hasUser && stay.UserId == userId;
        if (!IsPublic(stay) && !isOwner) return NotFoundResponse(StayNotFound);

        var dto = stay.Adapt<StayPublicDto>();
        if (!isAuth)
        {
            dto.OwnerName = null;
            dto.OwnerPhone = null;
        }
        else if (hasUser)
        {
            dto.HasReported = await unitOfWork.ListingReports.HasPendingReportFromReporterAsync(id, ListingKinds.Stay, userId);
        }
        return OkResponse(dto);
    }

    public static async Task<IResult> ReportStay(
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

        var stay = await unitOfWork.Stays.GetByIdAsync(id);
        if (stay == null || !IsPublic(stay)) return NotFoundResponse(StayNotFound);
        if (stay.UserId == userId) return BadRequestResponse("You cannot report your own stay");

        var reason = await unitOfWork.ReportReasons.GetByIdAsync(request.ReasonId);
        if (reason == null) return BadRequestResponse("Invalid reason");

        var reporter = await unitOfWork.Users.GetByIdAsync(userId);
        var owner = await unitOfWork.Users.GetByIdAsync(stay.UserId);

        var isFirstPending = !await unitOfWork.ListingReports.HasPendingReportForListingAsync(id, ListingKinds.Stay);

        var report = new ListingReport
        {
            Id = Guid.NewGuid(),
            ListingId = id,
            ListingType = ListingKinds.Stay,
            ReporterUserId = userId,
            ReporterName = reporter?.Name ?? "Unknown",
            ReporterMobile = reporter?.PhoneNumber ?? "",
            ReportedUserId = stay.UserId,
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
            return ConflictResponse("You have already reported this stay");
        }

        var message = new ReportFiledMessage
        {
            OwnerId = stay.UserId,
            ListingId = stay.Id,
            ListingType = ListingKinds.Stay,
            ReasonName = reason.Name,
            ListingTitle = stay.Name ?? "your stay",
            NotifyOwner = isFirstPending,
        };
        try { await publisher.PublishAsync("report.filed", JsonSerializer.Serialize(message)); }
        catch (Exception) { /* best-effort — report row is already saved regardless of push delivery */ }

        return CreatedResponse(new { reportId = report.Id }, $"/api/v1/admin/reports/{report.Id}");
    }

    // ---- Owner (CanListStays), always keyed on the caller's live stay ----

    public static async Task<IResult> GetMine(ClaimsPrincipal principal, IUnitOfWork unitOfWork)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var stay = await unitOfWork.Stays.GetLiveByUserAsync(userId);
        if (stay == null) return OkResponse<StayOwnerDto?>(null);
        return OkResponse(await ToOwnerDtoAsync(stay, unitOfWork));
    }

    public static async Task<IResult> UpsertBasic(
        UpsertStayBasicRequest request,
        ClaimsPrincipal principal,
        IValidator<UpsertStayBasicRequest> validator,
        IUnitOfWork unitOfWork,
        ApplicationDbContext db)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
            return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var stayType = await unitOfWork.StayTypes.GetByIdAsync(request.StayTypeId!.Value);
        if (stayType == null) return BadRequestResponse("Invalid stay type");

        var district = await unitOfWork.Districts.GetByIdAsync(request.DistrictId);
        if (district == null) return BadRequestResponse("Invalid district");

        City? city = null;
        if (request.CityId.HasValue)
        {
            city = await unitOfWork.Cities.GetByIdAsync(request.CityId.Value);
            if (city == null) return BadRequestResponse("Selected city does not exist");
            if (city.DistrictId != request.DistrictId)
                return BadRequestResponse("Selected city does not belong to the selected district");
        }

        var stay = await unitOfWork.Stays.GetLiveByUserAsync(userId);
        var isNew = stay == null;
        if (stay != null && !IsEditable(stay)) return LockedResponse(stay);

        var now = DateTime.UtcNow;
        stay ??= new StayListing
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Status = StayStatuses.Draft,
            AcceptingGuests = true,
            CreatedAt = now,
        };

        stay.Name = request.Name!.Trim();
        stay.StayTypeId = stayType.Id;
        stay.StayType = stayType;
        stay.VehicleAccess = request.VehicleAccess;
        stay.HostLivesHere = request.HostLivesHere;
        stay.Facilities = request.Facilities?.Distinct().ToArray() ?? Array.Empty<string>();
        stay.Description = request.Description;
        stay.Latitude = request.Latitude;
        stay.Longitude = request.Longitude;
        stay.Address = request.Address;
        stay.DistrictId = request.DistrictId;
        stay.CityId = request.CityId;
        stay.District = district;
        stay.City = city;
        stay.UpdatedAt = now;

        if (!isNew)
        {
            try { await unitOfWork.SaveChangesAsync(); }
            catch (DbUpdateConcurrencyException) { return Concurrent(); }
            return OkResponse(await ToOwnerDtoAsync(stay, unitOfWork));
        }

        // One live stay per account is enforced by a partial unique index; a racing second create
        // fails on SaveChanges and is reported as StayAlreadyExists below.
        db.StayListings.Add(stay);
        var baseSlug = SlugGenerator.Generate($"{stay.Name} {city?.Name ?? district.Name}");
        var inserted = await SlugGenerator.GenerateUniqueSlugWithRetryAsync(
            baseSlug,
            slug => stay.Slug = slug,
            async () =>
            {
                try
                {
                    await unitOfWork.SaveChangesAsync();
                    return true;
                }
                catch (DbUpdateException)
                {
                    return false;
                }
            });
        if (!inserted)
        {
            db.Entry(stay).State = EntityState.Detached;
            if (await db.StayListings.AnyAsync(s => s.UserId == userId && !s.IsDeleted))
                return ConflictResponse("One stay per account", StayErrorTypes.StayAlreadyExists);
            return ConflictResponse("Could not generate a unique link for this stay. Please try again.", "SLUG_GENERATION_FAILED");
        }

        return OkResponse(await ToOwnerDtoAsync(stay, unitOfWork));
    }

    public static async Task<IResult> UploadPhoto(
        IFormFile photo, ClaimsPrincipal principal, IUnitOfWork unitOfWork, ApplicationDbContext db, IPhotoService photoService)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var stay = await unitOfWork.Stays.GetLiveByUserAsync(userId);
        if (stay == null) return NotFoundResponse(StayNotFound);
        if (!IsEditable(stay)) return LockedResponse(stay);
        if (stay.Photos.Count >= StayLimits.MaxPhotos) return BadRequestResponse($"Maximum {StayLimits.MaxPhotos} photos allowed");
        if (photo.Length > 10 * 1024 * 1024) return BadRequestResponse("Photo size must not exceed 10MB");

        using var stream = photo.OpenReadStream();
        if (!IsValidImageMagicBytes(stream)) return BadRequestResponse("File must be a valid image (JPEG, PNG or WebP)");
        stream.Position = 0;
        // Same Cloudinary prefix as rooms/plots (user_{userId}/listing_{id}), so the shared
        // purge/account-deletion photo cleanup covers stays without changes.
        var (url, filePath) = await photoService.SavePhotoAsync(stream, photo.FileName, userId, stay.Id);

        var stayPhoto = new StayPhoto
        {
            Id = Guid.NewGuid(),
            StayListingId = stay.Id,
            PhotoUrl = url,
            FilePath = filePath,
            PhotoOrder = stay.Photos.Count == 0 ? 0 : stay.Photos.Max(p => p.PhotoOrder) + 1,
            UploadedAt = DateTime.UtcNow,
        };
        db.StayPhotos.Add(stayPhoto);
        await unitOfWork.SaveChangesAsync();

        return CreatedResponse(new { photoUrl = url, photoId = stayPhoto.Id }, url);
    }

    public static async Task<IResult> DeletePhoto(
        Guid photoId, ClaimsPrincipal principal, IUnitOfWork unitOfWork, ApplicationDbContext db, IPhotoService photoService)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var stay = await unitOfWork.Stays.GetLiveByUserAsync(userId);
        if (stay == null) return NotFoundResponse(StayNotFound);
        if (!IsEditable(stay)) return LockedResponse(stay);

        var photo = stay.Photos.FirstOrDefault(p => p.Id == photoId);
        if (photo == null) return NotFoundResponse("Photo not found");

        await photoService.DeletePhotoAsync(photo.FilePath);
        db.StayPhotos.Remove(photo);
        await unitOfWork.SaveChangesAsync();

        return NoContentResponse();
    }

    public static async Task<IResult> UpsertRooms(
        UpsertStayRoomsRequest request,
        ClaimsPrincipal principal,
        IValidator<UpsertStayRoomsRequest> validator,
        IUnitOfWork unitOfWork,
        ApplicationDbContext db)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            var error = validation.Errors[0];
            return error.PropertyName == nameof(UpsertStayRoomsRequest.Rooms)
                ? BadRequestResponse(error.ErrorMessage, StayErrorTypes.StayRoomLimit)
                : BadRequestResponse(error.ErrorMessage);
        }

        var stay = await unitOfWork.Stays.GetLiveByUserAsync(userId);
        if (stay == null) return NotFoundResponse(StayNotFound);
        if (!IsEditable(stay)) return LockedResponse(stay);
        if (!StaySetupRules.Step1Done(stay)) return StepLocked(1);

        // Full replace by position: slot N keeps its row (and RoomIndex N), extra rows are removed,
        // so the (StayListingId, RoomIndex) unique index never sees a transient duplicate.
        var now = DateTime.UtcNow;
        var existing = stay.Rooms.OrderBy(r => r.RoomIndex).ToList();
        for (var i = 0; i < request.Rooms.Count; i++)
        {
            var input = request.Rooms[i];
            var index = (short)(i + 1);
            var room = existing.FirstOrDefault(r => r.RoomIndex == index);
            if (room == null)
            {
                room = new StayRoom
                {
                    Id = Guid.NewGuid(),
                    StayListingId = stay.Id,
                    RoomIndex = index,
                    CreatedAt = now,
                };
                db.StayRooms.Add(room);
                stay.Rooms.Add(room);
            }
            room.PricePerNight = input.PricePerNight;
            room.MaxGuests = input.MaxGuests;
            room.RoomName = input.RoomName;
            room.Meals = input.Meals;
            room.UpdatedAt = now;
        }
        foreach (var extra in existing.Where(r => r.RoomIndex > request.Rooms.Count))
        {
            db.StayRooms.Remove(extra);
            stay.Rooms.Remove(extra);
        }

        RecomputeMinPrice(stay);
        stay.UpdatedAt = now;

        try { await unitOfWork.SaveChangesAsync(); }
        catch (DbUpdateConcurrencyException) { return Concurrent(); }

        return OkResponse(await ToOwnerDtoAsync(stay, unitOfWork));
    }

    public static async Task<IResult> UpsertRules(
        UpsertStayRulesRequest request,
        ClaimsPrincipal principal,
        IValidator<UpsertStayRulesRequest> validator,
        IUnitOfWork unitOfWork)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
            return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var stay = await unitOfWork.Stays.GetLiveByUserAsync(userId);
        if (stay == null) return NotFoundResponse(StayNotFound);
        if (!IsEditable(stay)) return LockedResponse(stay);
        if (!StaySetupRules.Step1Done(stay)) return StepLocked(1);
        if (!StaySetupRules.RoomsDone(stay)) return StepLocked(2);

        var now = DateTime.UtcNow;
        stay.CheckInTime = TimeOnly.ParseExact(request.CheckInTime!, "HH:mm", CultureInfo.InvariantCulture);
        stay.CheckOutTime = TimeOnly.ParseExact(request.CheckOutTime!, "HH:mm", CultureInfo.InvariantCulture);
        stay.PetsAllowed = request.PetsAllowed;
        stay.SmokingAllowed = request.SmokingAllowed;
        stay.AlcoholAllowed = request.AlcoholAllowed;
        stay.UnmarriedCouplesAllowed = request.UnmarriedCouplesAllowed;
        stay.IdProofRequired = request.IdProofRequired;
        stay.CancellationPolicy = request.CancellationPolicy;
        stay.RulesSavedAt = now;
        stay.UpdatedAt = now;

        try { await unitOfWork.SaveChangesAsync(); }
        catch (DbUpdateConcurrencyException) { return Concurrent(); }

        return OkResponse(await ToOwnerDtoAsync(stay, unitOfWork));
    }

    public static async Task<IResult> Submit(
        ClaimsPrincipal principal, IUnitOfWork unitOfWork, IRabbitMqPublisher publisher)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var stay = await unitOfWork.Stays.GetLiveByUserAsync(userId);
        if (stay == null) return NotFoundResponse(StayNotFound);
        if (!IsEditable(stay)) return LockedResponse(stay);
        if (!StaySetupRules.Step1Done(stay)) return StepLocked(1);
        if (!StaySetupRules.RoomsDone(stay)) return StepLocked(2);
        if (!StaySetupRules.RulesDone(stay)) return StepLocked(3);

        var now = DateTime.UtcNow;
        stay.Status = StayStatuses.PendingReview;
        stay.SubmittedAt = now;
        stay.RejectedReason = null;
        stay.UpdatedAt = now;

        try { await unitOfWork.SaveChangesAsync(); }
        catch (DbUpdateConcurrencyException) { return Concurrent(); }

        try
        {
            var message = new GoLiveRequestedMessage { ListingId = stay.Id, ListingType = ListingKinds.Stay };
            await publisher.PublishAsync("golive.requested", JsonSerializer.Serialize(message));
        }
        catch { /* best-effort — the PendingReview row is already saved */ }

        return OkResponse(await ToOwnerDtoAsync(stay, unitOfWork));
    }

    public static async Task<IResult> UpdateAccepting(
        UpdateStayAcceptingRequest request,
        ClaimsPrincipal principal,
        IValidator<UpdateStayAcceptingRequest> validator,
        IUnitOfWork unitOfWork,
        IServiceProvider sp)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
            return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var stay = await unitOfWork.Stays.GetLiveByUserAsync(userId);
        if (stay == null) return NotFoundResponse(StayNotFound);
        if (stay.Status != StayStatuses.Approved)
            return ConflictResponse("Your stay is not live yet.", StayErrorTypes.StayNotApproved);

        stay.AcceptingGuests = request.AcceptingGuests!.Value;
        stay.UpdatedAt = DateTime.UtcNow;

        try { await unitOfWork.SaveChangesAsync(); }
        catch (DbUpdateConcurrencyException) { return Concurrent(); }

        await StayCache.InvalidateAsync(sp.GetService<IConnectionMultiplexer>(), stay.DistrictId);
        return OkResponse(await ToOwnerDtoAsync(stay, unitOfWork));
    }

    public static async Task<IResult> UpdateRoomPrice(
        Guid roomId,
        UpdateStayRoomPriceRequest request,
        ClaimsPrincipal principal,
        IValidator<UpdateStayRoomPriceRequest> validator,
        IUnitOfWork unitOfWork,
        IServiceProvider sp)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
            return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var stay = await unitOfWork.Stays.GetLiveByUserAsync(userId);
        if (stay == null) return NotFoundResponse(StayNotFound);
        if (stay.Status == StayStatuses.PendingReview) return LockedResponse(stay);

        var room = stay.Rooms.FirstOrDefault(r => r.Id == roomId);
        if (room == null) return NotFoundResponse("Room not found");

        var now = DateTime.UtcNow;
        room.PricePerNight = request.PricePerNight;
        room.UpdatedAt = now;
        RecomputeMinPrice(stay);
        stay.UpdatedAt = now;

        try { await unitOfWork.SaveChangesAsync(); }
        catch (DbUpdateConcurrencyException) { return Concurrent(); }

        if (stay.Status == StayStatuses.Approved)
            await StayCache.InvalidateAsync(sp.GetService<IConnectionMultiplexer>(), stay.DistrictId);
        return OkResponse(await ToOwnerDtoAsync(stay, unitOfWork));
    }

    public static async Task<IResult> DeleteMine(
        ClaimsPrincipal principal, IUnitOfWork unitOfWork, IPhotoService photoService, IServiceProvider sp)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var stay = await unitOfWork.Stays.GetLiveByUserAsync(userId);
        if (stay == null) return NotFoundResponse(StayNotFound);

        await photoService.DeleteRoomPhotosAsync(userId, stay.Id);

        stay.IsDeleted = true;
        stay.DeletedAt = DateTime.UtcNow;
        stay.UpdatedAt = DateTime.UtcNow;

        try { await unitOfWork.SaveChangesAsync(); }
        catch (DbUpdateConcurrencyException) { return Concurrent(); }

        await unitOfWork.StayJobs.CloseActiveForStayAsync(stay.Id);
        await unitOfWork.ListingReports.AutoResolvePendingForListingAsync(stay.Id, ListingKinds.Stay);
        await StayCache.InvalidateAsync(sp.GetService<IConnectionMultiplexer>(), stay.DistrictId);

        return NoContentResponse();
    }

    public static async Task<IResult> GetStayReports(
        Guid id, ClaimsPrincipal principal, IUnitOfWork unitOfWork, int page = 1, int pageSize = 20)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var stay = await unitOfWork.Stays.GetByIdAsync(id);
        if (stay == null) return NotFoundResponse(StayNotFound);
        if (stay.UserId != userId) return ForbiddenResponse("You do not own this stay");

        pageSize = Math.Clamp(pageSize, 1, 50);
        page = Math.Max(1, page);
        var paged = await unitOfWork.ListingReports.GetPagedForListingAsync(id, ListingKinds.Stay, page, pageSize);
        var items = paged.Items.Select(r => new ListingReportDto
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
        }).ToList();
        return OkResponse(new { items, hasMore = paged.HasMore });
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
}
