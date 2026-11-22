using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using FluentValidation;
using Mapster;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Interfaces.Owner;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Core.Utils;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Infrastructure.Services.Owner;
using RentNearBy.Infrastructure.Services.Owner.Be3;
using StackExchange.Redis;
using RentNearBy.Api.Handlers.Owner;
using RentNearBy.Api.Handlers.Photos;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

// Redis keys for the stay feeds. Busted on every change that moves a stay in or out of the public
// set (approve, reject, accepting toggle, price edit, delete, report takedown, role purge): the district's
// browse/nearby/nearest entries by one generation INCR (FeedCache), the recent list by a direct delete.
public static class StayCache
{
    public static readonly TimeSpan BrowseTtl = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan NearbyTtl = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan NearestTtl = TimeSpan.FromSeconds(60);

    public static string BrowseKey(Guid districtId, Guid? cityId, string? meals, Guid? stayTypeId, string sort, int pageSize)
        => $"stays:browse:{districtId:N}:{cityId:N}:{meals}:{stayTypeId:N}:{sort}:{pageSize}";

    // Home "Recently added" stays: district-free, holds the newest RecentMax rows.
    public const string RecentKey = "home:recentStays";
    public const int RecentMax = 20;
    public static readonly TimeSpan RecentTtl = TimeSpan.FromMinutes(3);

    // Both hold the candidate set of a whole grid cell (GeoCacheKey); the exact cut and distances are made per caller.
    public static string NearbyKey(Guid districtId, Guid? stayTypeId, double radius, GeoCell cell)
        => string.Create(CultureInfo.InvariantCulture, $"stays:nearby:{districtId}:{stayTypeId:N}:{GeoCacheKey.RadiusKey(radius):F1}:{cell.Key}");

    public static string NearestKey(Guid districtId, Guid? stayTypeId, int count, GeoCell cell)
        => string.Create(CultureInfo.InvariantCulture, $"stays:nearest:{districtId}:{stayTypeId:N}:{count}:{cell.Key}");

    // One entry per city (nearby and nearest share it); the exact order and distances are made per caller.
    public static string CityKey(long cityKey, Guid? stayTypeId) => $"stays:city:{cityKey}:{stayTypeId:N}";
    public static readonly TimeSpan CityTtl = TimeSpan.FromSeconds(60);

    // Distance-only nearby/nearest (no district, no city): the cell candidate set, stamped with ListingCacheKeys.StayAllGen.
    public static string AnyNearbyKey(Guid? stayTypeId, double radius, GeoCell cell)
        => string.Create(CultureInfo.InvariantCulture, $"stays:nearby:any:{stayTypeId:N}:{GeoCacheKey.RadiusKey(radius):F1}:{cell.Key}");

    public static string AnyNearestKey(Guid? stayTypeId, int count, GeoCell cell)
        => string.Create(CultureInfo.InvariantCulture, $"stays:nearest:any:{stayTypeId:N}:{count}:{cell.Key}");

    public static string GenKey(Guid districtId) => ListingCacheKeys.StayGen(districtId);

    public static async Task InvalidateAsync(IConnectionMultiplexer? redis, Guid? districtId)
    {
        await RentNearBy.Infrastructure.Services.TripSupplyCache.BumpAsync(redis, districtId);
        if (redis == null) return;
        try
        {
            await redis.GetDatabase().KeyDeleteAsync(RecentKey);
        }
        catch { /* best-effort: TTL (<= 60s) covers Redis failures */ }
        var gens = districtId is { } id ? new[] { GenKey(id), ListingCacheKeys.StayAllGen } : new[] { ListingCacheKeys.StayAllGen };
        await FeedCache.BumpAsync(redis, gens);
    }
}

// Stays of one city, paid rank first then nearest to the point; distances are the caller's own.
public static class StayCityRank
{
    public static List<StayNearbyDto> Order(IReadOnlyList<StayNearbyDto> stays, double lat, double lng, int? count)
    {
        foreach (var s in stays) s.DistanceKm = GeoMath.HaversineKm(lat, lng, (double)s.Latitude, (double)s.Longitude);
        var ordered = stays.OrderByDescending(s => s.PriorityRank).ThenBy(s => s.DistanceKm).ThenBy(s => s.Id);
        return (count is { } n ? ordered.Take(n) : ordered).ToList();
    }
}

public static class StayHandlers
{
    private static readonly TimeSpan ReportSubmitWindow = TimeSpan.FromHours(1);
    private const int ReportSubmitMax = 5;
    private static readonly TimeSpan NearestRateLimitWindow = TimeSpan.FromMinutes(10);
    private const int NearestRateLimitMax = 20;
    private const int NearbyRateLimitMax = 120;
    public const string DetailRateLimitPrefix = "stays:detail:ip";
    public const int DetailRateLimitMax = 120;
    private const int DefaultPageSize = 10;
    private const int MaxPageSize = 30;
    private const string StayNotFound = "Stay not found";

    private static readonly HashSet<string> SortValues = new() { "newest", "price_asc", "price_desc" };

    private static bool IsPublic(StayListing s) => !s.IsDeleted && s.Status == StayStatuses.Approved;

    private static bool IsEditable(StayListing s) => s.Status is StayStatuses.Draft or StayStatuses.Rejected;

    // Live edit (contract 3.5, AU-5): rooms, photos and rules save at once on an Approved stay; only review keeps them locked.
    private static bool IsLiveEditable(StayListing s) => s.Status != StayStatuses.PendingReview;

    private sealed class PhotoCountRow
    {
        public int Visible { get; set; }
        public int All { get; set; }
        public int MaxOrder { get; set; }
    }

    private static IResult LockedResponse(StayListing s) => LockedResponse(s.Status);

    private static IResult LockedResponse(string status) => status == StayStatuses.PendingReview
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
        var (cached, generation) = await FeedCache.ReadAsync(redis, cacheKey, StayCache.GenKey(districtId));
        if (cached != null)
        {
            try { result = JsonSerializer.Deserialize<BrowsePage<StayBrowseDto>>(cached); }
            catch (JsonException) { result = null; }
        }

        if (result == null)
        {
            result = await unitOfWork.Stays.BrowsePagedAsync(districtId, cityId, mealsFilter, stayTypeId, sort, pageNo, size, cursor);
            await FeedCache.WriteAsync(redis, cacheKey, generation, JsonSerializer.Serialize(result), StayCache.BrowseTtl);
        }

        return OkResponse(new { items = result.Items, hasMore = result.HasMore, nextCursor = result.NextCursor });
    }

    // Anonymous callers get neither OwnerName nor OwnerPhone. The cache holds full rows, so the
    // redaction runs per request after the read.
    private static void RedactForAnonymous(List<StayNearbyDto> items)
    {
        foreach (var d in items) { d.OwnerName = null; d.OwnerPhone = null; }
    }

    private static async Task<(List<StayNearbyDto>? Items, long? Generation)> ReadNearbyCacheAsync(
        IConnectionMultiplexer? redis, string key, string genKey)
    {
        var (cached, generation) = await FeedCache.ReadAsync(redis, key, genKey);
        if (cached == null) return (null, generation);
        try { return (JsonSerializer.Deserialize<List<StayNearbyDto>>(cached), generation); }
        catch (JsonException) { return (null, generation); }
    }

    private static Task WriteNearbyCacheAsync(
        IConnectionMultiplexer? redis, string key, long? generation, List<StayNearbyDto> items, TimeSpan ttl)
        => FeedCache.WriteAsync(redis, key, generation, JsonSerializer.Serialize(items), ttl);

    // With cityKey the stays are the ones whose own nearest city is that city (any district, no radius); the pin only sorts.
    public static async Task<IResult> GetNearby(
        double? latitude, double? longitude, double? radius, Guid? districtId, Guid? stayTypeId, long? cityKey,
        IUnitOfWork unitOfWork, ICityResolver resolver, ClaimsPrincipal principal, IServiceProvider sp,
        IRateLimitService rateLimiter, HttpContext httpContext, int? guests = null,
        bool? groundFloor = null, int? rooms = null, int? nights = null, DateOnly? checkIn = null)
    {
        if (PartyError(guests, rooms, nights) is { } partyError) return partyError;
        if (CheckInError(checkIn) is { } dateError) return dateError;
        if (cityKey != null)
        {
            if (CoordinateError(latitude, longitude)) return BadRequestResponse("Invalid coordinates");
            return await CityStaysAsync(cityKey.Value, latitude, longitude, null, stayTypeId, unitOfWork, resolver, principal, sp,
                guests, groundFloor, rooms, nights, checkIn);
        }
        if (radius is not { } radiusKm || radiusKm < 1 || radiusKm > 50)
            return BadRequestResponse("Radius must be between 1 and 50 km");
        if (latitude is not { } lat || longitude is not { } lng || double.IsNaN(lat) || double.IsNaN(lng)
            || lat < -90 || lat > 90 || lng < -180 || lng > 180)
            return BadRequestResponse("Invalid coordinates");
        if (districtId is not { } district)
        {
            if (await RateLimited(rateLimiter, httpContext, "stays:nearby:ip", NearbyRateLimitMax) is { } limit) return limit;
            return await AnyNearbyAsync(lat, lng, radiusKm, stayTypeId, unitOfWork, principal, sp, guests, groundFloor, rooms, nights, checkIn);
        }
        return await DistrictNearbyAsync(lat, lng, radiusKm, district, stayTypeId, unitOfWork, principal, sp, guests, groundFloor, rooms, nights, checkIn);
    }

    // FB-27: one bound for every public read that prices by date.
    private static IResult? CheckInError(DateOnly? checkIn)
        => StayDateRules.CheckInError(checkIn, IstTime.TodayIst(DateTime.UtcNow)) is { } message ? BadRequestResponse(message) : null;

    private static async Task<IResult?> RateLimited(IRateLimitService rateLimiter, HttpContext httpContext, string prefix, int max)
    {
        var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var rl = await rateLimiter.CheckAsync($"{prefix}:{ip}", max, NearestRateLimitWindow);
        if (rl.IsAllowed) return null;
        httpContext.Response.Headers["Retry-After"] = ((int)rl.RetryAfter!.Value.TotalSeconds).ToString();
        return TooManyRequestsResponse();
    }

    // No district, no city: every approved stay inside the radius (cached per ~110 m cell, one global generation).
    private static async Task<IResult> AnyNearbyAsync(
        double latitude, double longitude, double radius, Guid? stayTypeId,
        IUnitOfWork unitOfWork, ClaimsPrincipal principal, IServiceProvider sp, int? guests, bool? groundFloor, int? rooms, int? nights,
        DateOnly? checkIn)
    {
        var redis = sp.GetService<IConnectionMultiplexer>();
        var cell = GeoCacheKey.Cell(latitude, longitude);
        var cacheKey = StayCache.AnyNearbyKey(stayTypeId, radius, cell);

        var (candidates, generation) = await ReadNearbyCacheAsync(redis, cacheKey, ListingCacheKeys.StayAllGen);
        if (candidates == null)
        {
            candidates = (await unitOfWork.Stays.GetNearbyAsync(
                cell.CenterLat, cell.CenterLng, GeoCacheKey.CandidateRadiusKm(cell, radius), null, stayTypeId)).ToList();
            await WriteNearbyCacheAsync(redis, cacheKey, generation, candidates, StayCache.NearbyTtl);
        }

        var items = StayGeoRefine.WithinRadius(candidates, latitude, longitude, radius);
        items = StayNearbyRooms.Apply(items, guests, groundFloor == true, rooms, nights ?? 1);
        items = Dated(sp.GetService<IStayDatedPricing>(), items, checkIn, nights, guests, rooms, groundFloor);
        if (principal.Identity?.IsAuthenticated != true) RedactForAnonymous(items);
        return OkResponse(new { items = StayGeoRefine.WithMapThumbs(items) });
    }

    private static bool CoordinateError(double? latitude, double? longitude)
        => latitude.HasValue != longitude.HasValue
           || latitude is < -90 or > 90 || longitude is < -180 or > 180
           || (latitude is { } a && double.IsNaN(a)) || (longitude is { } b && double.IsNaN(b));

    // count: nearest N (null = every stay of the city), both sorted paid rank first then nearest to the pin (or the city centre).
    private static async Task<IResult> CityStaysAsync(
        long cityKey, double? latitude, double? longitude, int? count, Guid? stayTypeId, IUnitOfWork unitOfWork, ICityResolver resolver,
        ClaimsPrincipal principal, IServiceProvider sp, int? guests, bool? groundFloor, int? rooms, int? nights, DateOnly? checkIn)
    {
        var pin = await resolver.FindAsync(cityKey);
        if (!pin.Available) return ErrorResponse(503, "Cities are not available right now", TripErrorTypes.CitiesUnavailable);
        if (pin.City is not { } city) return OkResponse(new { items = new List<StayNearbyDto>() });

        var redis = sp.GetService<IConnectionMultiplexer>();
        var cacheKey = StayCache.CityKey(cityKey, stayTypeId);
        var (candidates, generation) = await ReadCityStaysAsync(redis, cacheKey);
        if (candidates == null)
        {
            var fetch = await CityListings.StaysAsync(unitOfWork.Stays, resolver, city, stayTypeId);
            if (fetch.Capped)
                sp.GetService<ILoggerFactory>()?.CreateLogger("StayCity").LogWarning("stays.city.capped {CityKey} {Max}", cityKey, CityListings.MaxCityFetch);
            candidates = fetch.Items.ToList();
            await FeedCache.WriteAsync(redis, cacheKey, generation, JsonSerializer.Serialize(candidates), StayCache.CityTtl);
        }

        var items = StayCityRank.Order(candidates, latitude ?? city.Latitude, longitude ?? city.Longitude, count);
        items = StayNearbyRooms.Apply(items, guests, groundFloor == true, rooms, nights ?? 1);
        items = Dated(sp.GetService<IStayDatedPricing>(), items, checkIn, nights, guests, rooms, groundFloor);
        if (principal.Identity?.IsAuthenticated != true) RedactForAnonymous(items);
        if (principal.FindFirst(ClaimTypes.Role)?.Value != UserRoles.StayOwner) sp.GetService<RentNearBy.Infrastructure.Services.Owner.Be4.OwnerShownCounter>()?.AddListed(items.Select(s => s.Id));
        return OkResponse(new { items = StayGeoRefine.WithMapThumbs(items) });
    }

    private static async Task<(List<StayNearbyDto>? Items, long? Generation)> ReadCityStaysAsync(IConnectionMultiplexer? redis, string key)
    {
        var (cached, generation) = await FeedCache.ReadAsync(redis, key, TripSupplyCache.AllGenKey);
        if (cached == null) return (null, generation);
        try { return (JsonSerializer.Deserialize<List<StayNearbyDto>>(cached), generation); }
        catch (JsonException) { return (null, generation); }
    }

    private static async Task<IResult> DistrictNearbyAsync(
        double latitude, double longitude, double radius, Guid districtId, Guid? stayTypeId,
        IUnitOfWork unitOfWork, ClaimsPrincipal principal, IServiceProvider sp, int? guests, bool? groundFloor, int? rooms, int? nights,
        DateOnly? checkIn)
    {

        var redis = sp.GetService<IConnectionMultiplexer>();
        var isAuth = principal.Identity?.IsAuthenticated == true;
        var cell = GeoCacheKey.Cell(latitude, longitude);
        var cacheKey = StayCache.NearbyKey(districtId, stayTypeId, radius, cell);

        var (candidates, generation) = await ReadNearbyCacheAsync(redis, cacheKey, StayCache.GenKey(districtId));
        if (candidates == null)
        {
            candidates = (await unitOfWork.Stays.GetNearbyAsync(
                cell.CenterLat, cell.CenterLng, GeoCacheKey.CandidateRadiusKm(cell, radius), districtId, stayTypeId)).ToList();
            await WriteNearbyCacheAsync(redis, cacheKey, generation, candidates, StayCache.NearbyTtl);
        }

        var items = StayGeoRefine.WithinRadius(candidates, latitude, longitude, radius);
        items = StayNearbyRooms.Apply(items, guests, groundFloor == true, rooms, nights ?? 1);
        items = Dated(sp.GetService<IStayDatedPricing>(), items, checkIn, nights, guests, rooms, groundFloor);
        if (!isAuth) RedactForAnonymous(items);
        return OkResponse(new { items = StayGeoRefine.WithMapThumbs(items) });
    }

    // Party params of the nearby quote, each optional: guests up to StayPartyFit.MaxGuests, rooms 1..MaxRoomsWanted, nights 1..30.
    private static IResult? PartyError(int? guests, int? rooms, int? nights)
    {
        if (guests is > StayPartyFit.MaxGuests) return BadRequestResponse($"Guests must be at most {StayPartyFit.MaxGuests}");
        if (rooms is < 1 or > StayPartyFit.MaxRoomsWanted) return BadRequestResponse($"Rooms must be 1 to {StayPartyFit.MaxRoomsWanted}");
        if (nights is < 1 or > 30) return BadRequestResponse("Nights must be 1 to 30");
        return null;
    }

    // Owner roles T1 (lane BE2): the one dated-price pass of every stay list read, after the party quote. No service = unchanged.
    private static List<StayNearbyDto> Dated(
        IStayDatedPricing? pricing, List<StayNearbyDto> items, DateOnly? checkIn, int? nights, int? guests, int? rooms, bool? groundFloor)
        => pricing == null ? items : pricing.Apply(items, checkIn, nights ?? 1, guests, rooms, groundFloor == true, IstTime.TodayIst(DateTime.UtcNow));

    public static async Task<IResult> GetNearest(
        double? latitude, double? longitude, Guid? districtId, Guid? stayTypeId, long? cityKey,
        IUnitOfWork unitOfWork, ICityResolver resolver, ClaimsPrincipal principal, IServiceProvider sp,
        IRateLimitService rateLimiter, HttpContext httpContext, int count = 5, int? guests = null, bool? groundFloor = null,
        int? rooms = null, int? nights = null, DateOnly? checkIn = null)
    {
        if (count < 1 || count > 10)
            return BadRequestResponse("Count must be between 1 and 10");
        if (PartyError(guests, rooms, nights) is { } partyError) return partyError;
        if (CheckInError(checkIn) is { } dateError) return dateError;
        if (cityKey == null)
        {
            if (latitude is not (>= -90 and <= 90) || longitude is not (>= -180 and <= 180))
                return BadRequestResponse("Invalid coordinates");
        }
        else if (CoordinateError(latitude, longitude)) return BadRequestResponse("Invalid coordinates");

        if (await RateLimited(rateLimiter, httpContext, "stays:nearest:ip", NearestRateLimitMax) is { } limited) return limited;

        if (cityKey != null)
            return await CityStaysAsync(cityKey.Value, latitude, longitude, count, stayTypeId, unitOfWork, resolver, principal, sp,
                guests, groundFloor, rooms, nights, checkIn);

        var redis = sp.GetService<IConnectionMultiplexer>();
        var isAuth = principal.Identity?.IsAuthenticated == true;
        var lat = latitude!.Value;
        var lng = longitude!.Value;
        var cell = GeoCacheKey.Cell(lat, lng);

        if (districtId is not { } districtGuid)
            return await AnyNearestAsync(lat, lng, count, stayTypeId, cell, unitOfWork, redis, isAuth, guests, groundFloor, rooms, nights,
                checkIn, sp.GetService<IStayDatedPricing>());

        var cacheKey = StayCache.NearestKey(districtGuid, stayTypeId, count, cell);

        var (candidates, generation) = await ReadNearbyCacheAsync(redis, cacheKey, StayCache.GenKey(districtGuid));
        if (candidates == null)
        {
            var knn = (await unitOfWork.Stays.GetNearestAsync(cell.CenterLat, cell.CenterLng, count, districtGuid, stayTypeId)).ToList();
            candidates = StayGeoRefine.NearestCandidateRadiusKm(cell, knn) is { } candidateKm
                ? (await unitOfWork.Stays.GetNearbyAsync(cell.CenterLat, cell.CenterLng, candidateKm, districtGuid, stayTypeId)).ToList()
                : [];
            await WriteNearbyCacheAsync(redis, cacheKey, generation, candidates, StayCache.NearestTtl);
        }

        var items = StayGeoRefine.Nearest(candidates, lat, lng, count);
        items = StayNearbyRooms.Apply(items, guests, groundFloor == true, rooms, nights ?? 1);
        items = Dated(sp.GetService<IStayDatedPricing>(), items, checkIn, nights, guests, rooms, groundFloor);
        if (!isAuth) RedactForAnonymous(items);
        return OkResponse(new { items = StayGeoRefine.WithMapThumbs(items) });
    }

    // No district, no city: the nearest stays by distance, only within StayLimits.NearestMaxKm of the caller.
    private static async Task<IResult> AnyNearestAsync(
        double lat, double lng, int count, Guid? stayTypeId, GeoCell cell, IUnitOfWork unitOfWork, IConnectionMultiplexer? redis,
        bool isAuth, int? guests, bool? groundFloor, int? rooms, int? nights, DateOnly? checkIn, IStayDatedPricing? pricing)
    {
        var cacheKey = StayCache.AnyNearestKey(stayTypeId, count, cell);
        var (candidates, generation) = await ReadNearbyCacheAsync(redis, cacheKey, ListingCacheKeys.StayAllGen);
        if (candidates == null)
        {
            var knn = (await unitOfWork.Stays.GetNearestAsync(
                cell.CenterLat, cell.CenterLng, count, null, stayTypeId, StayGeoRefine.CentreCapKm(cell, StayLimits.NearestMaxKm))).ToList();
            candidates = StayGeoRefine.NearestCandidateRadiusKm(cell, knn) is { } candidateKm
                ? (await unitOfWork.Stays.GetNearbyAsync(cell.CenterLat, cell.CenterLng, candidateKm, null, stayTypeId)).ToList()
                : [];
            await WriteNearbyCacheAsync(redis, cacheKey, generation, candidates, StayCache.NearestTtl);
        }

        var items = StayGeoRefine.NearestWithin(candidates, lat, lng, count, StayLimits.NearestMaxKm);
        items = StayNearbyRooms.Apply(items, guests, groundFloor == true, rooms, nights ?? 1);
        items = Dated(pricing, items, checkIn, nights, guests, rooms, groundFloor);
        if (!isAuth) RedactForAnonymous(items);
        return OkResponse(new { items = StayGeoRefine.WithMapThumbs(items) });
    }

    // Share-link resolver. StayPublicSlugDto carries no owner fields, and the repository only
    // returns Approved, non-deleted stays, so unreviewed content never leaks through a slug.
    public static async Task<IResult> GetBySlug(
        string slug, IUnitOfWork unitOfWork, ClaimsPrincipal principal, IServiceProvider sp, IRateLimitService rateLimiter, HttpContext httpContext,
        DateOnly? checkIn = null, int? nights = null, CancellationToken ct = default)
    {
        // FB-53: per-address read limit on the two detail routes (the "Shown" counter and the photo graph are behind them).
        if (await RateLimited(rateLimiter, httpContext, DetailRateLimitPrefix, DetailRateLimitMax) is { } limited) return limited;
        if (nights is < 1 or > 30) return BadRequestResponse("Nights must be 1 to 30");
        if (CheckInError(checkIn) is { } dateError) return dateError;
        var stay = await unitOfWork.Stays.GetBySlugWithAllAsync(slug);
        if (stay == null) return NotFoundResponse(StayNotFound);
        var dto = stay.Adapt<StayPublicSlugDto>();
        if (sp.GetService<IStayDetailPricing>() is { } pricing)
            await pricing.ApplyAsync(dto, checkIn, nights ?? 1, IstTime.TodayIst(DateTime.UtcNow), ct);
        var viewer = UsersHandlers.TryGetUserId(principal, out var viewerId) ? viewerId : (Guid?)null;
        if (viewer != stay.UserId) sp.GetService<RentNearBy.Infrastructure.Services.Owner.Be4.OwnerShownCounter>()?.AddViewed(stay.Id, stay.UserId, viewer, httpContext.Connection.RemoteIpAddress?.ToString());
        return OkResponse(dto);
    }

    public static async Task<IResult> GetById(
        Guid id, IUnitOfWork unitOfWork, ClaimsPrincipal principal, IServiceProvider sp, IRateLimitService rateLimiter, HttpContext httpContext,
        DateOnly? checkIn = null, int? nights = null, CancellationToken ct = default)
    {
        if (await RateLimited(rateLimiter, httpContext, DetailRateLimitPrefix, DetailRateLimitMax) is { } limited) return limited;
        if (nights is < 1 or > 30) return BadRequestResponse("Nights must be 1 to 30");
        if (CheckInError(checkIn) is { } dateError) return dateError;
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
        if (sp.GetService<IStayDetailPricing>() is { } pricing)
            await pricing.ApplyAsync(dto, checkIn, nights ?? 1, IstTime.TodayIst(DateTime.UtcNow), ct);
        if (!isOwner) sp.GetService<RentNearBy.Infrastructure.Services.Owner.Be4.OwnerShownCounter>()?.AddViewed(stay.Id, stay.UserId, hasUser ? userId : null, httpContext.Connection.RemoteIpAddress?.ToString());
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
        var tripEnquiryId = await TripLifecycleHandlers.OwnTripEnquiryIdAsync(unitOfWork, request.TripEnquiryId, id, userId);

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
            TripEnquiryId = tripEnquiryId,
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

    // M1 (ungated, contract R-5): the owner's own stay with the hub extras (hidden arrays, meters, pending change, rows).
    public static async Task<IResult> GetMine(ClaimsPrincipal principal, IUnitOfWork unitOfWork, StayHubReader hub)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var stay = await unitOfWork.Stays.GetLiveByUserReadAsync(userId);
        if (stay == null) return OkResponse<StayOwnerDto?>(null);
        return OkResponse(await ToHubDtoAsync(stay, unitOfWork, hub));
    }

    // FB-16: every owner write that returns the stay answers this shape, so the hub keeps its meters and hidden rows.
    private static async Task<StayOwnerDto> ToHubDtoAsync(StayListing stay, IUnitOfWork unitOfWork, StayHubReader hub, PlanLimits? limits = null)
    {
        var dto = await ToOwnerDtoAsync(stay, unitOfWork);
        await hub.FillAsync(dto, stay, limits);
        return dto;
    }

    public static async Task<IResult> UpsertBasic(
        UpsertStayBasicRequest request,
        ClaimsPrincipal principal,
        IValidator<UpsertStayBasicRequest> validator,
        IUnitOfWork unitOfWork,
        ApplicationDbContext db,
        IPlanEntitlementService entitlements,
        StayHubReader hub,
        IOwnerNotifier notifier,
        IServiceProvider sp)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
            return validation.Errors[0].ErrorCode == OwnerErrorTypes.PhoneNotAllowed
                ? BadRequestResponse(validation.Errors[0].ErrorMessage, OwnerErrorTypes.PhoneNotAllowed)
                : BadRequestResponse(validation.Errors[0].ErrorMessage);

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
        if (stay != null && !IsEditable(stay) && stay.Status != StayStatuses.Approved) return LockedResponse(stay);

        var check = BusinessOnboardingRules.ForSave(
            await BusinessOnboardingGate.CheckAsync(db, request.DistrictId, request.Latitude, request.Longitude),
            stay?.EverApproved ?? false, stay?.DistrictId, request.DistrictId, stay?.Latitude, stay?.Longitude, request.Latitude, request.Longitude);
        if (BusinessOnboardingRules.IsOutsideDistrict(check))
            return BadRequestResponse("Pin your stay inside the selected district", StayErrorTypes.StayOutsideDistrict);
        if (check == OnboardingCheck.OnboardingClosed)
            return BadRequestResponse(BusinessOnboardingRules.OnboardingClosedMessage, BusinessOnboardingRules.OnboardingClosedType);

        // Registration: the type is always sent; number null = keep the stored one (required when none is
        // stored or the type changed); GSTIN null = keep, "" = clear.
        var regType = await unitOfWork.StayRegistrationTypes.GetByIdAsync(request.RegistrationTypeId!.Value);
        if (regType == null || (!regType.IsActive && stay?.RegistrationTypeId != regType.Id))
            return BadRequestResponse(StayRegistrationRules.TypeInvalidMessage);

        var typeChanged = stay?.RegistrationTypeId != regType.Id;
        var numberNew = request.RegistrationNumber == null
            ? null
            : StayRegistrationRules.NormalizeForType(regType.Code, request.RegistrationNumber);
        if (numberNew == null && (stay == null || typeChanged))
            return BadRequestResponse(StayRegistrationRules.RegistrationRequiredMessage, StayErrorTypes.StayRegistrationRequired);
        if (numberNew != null && !StayRegistrationRules.IsValidForType(regType.Code, numberNew))
            return BadRequestResponse(StayRegistrationRules.MessageForType(regType.Code));

        string? otherName = null;
        if (regType.RequiresOtherName)
        {
            otherName = StayRegistrationRules.NormalizeOtherName(request.RegistrationOtherName);
            if (otherName.Length == 0) return BadRequestResponse(StayRegistrationRules.OtherNameRequiredMessage);
            if (!StayRegistrationRules.IsValidOtherName(otherName))
                return BadRequestResponse(StayRegistrationRules.OtherNameInvalidMessage);
        }

        var gstin = request.Gstin == null
            ? stay?.Gstin
            : string.IsNullOrWhiteSpace(request.Gstin) ? null : StayRegistrationRules.NormalizeGstin(request.Gstin);

        var now = DateTime.UtcNow;
        if (stay is { Status: StayStatuses.Approved })
        {
            var asked = new StayReviewValues(request.Name!.Trim(), regType.Id, regType.Name, otherName, numberNew ?? stay.RegistrationNumber, gstin,
                request.Latitude, request.Longitude, request.Address, request.DistrictId, district.Name, request.CityId, city?.Name);
            return await SaveLiveBasicAsync(stay, request, stayType, asked, now, unitOfWork, notifier, hub, sp);
        }
        if (stay == null)
        {
            var limits = await entitlements.GetLimitsAsync(userId, ListingKinds.Stay);
            stay = new StayListing
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Status = StayStatuses.Draft,
                AcceptingGuests = true,
                PriorityRank = limits.PriorityRank,
                IsFeatured = limits.IsFeatured,
                RegistrationTypeId = regType.Id,
                RegistrationTypeName = regType.Name,
                RegistrationNumber = numberNew!,
                CreatedAt = now,
            };
        }

        stay.Name = request.Name!.Trim();
        stay.StayTypeId = stayType.Id;
        stay.StayType = stayType;
        stay.VehicleAccess = request.VehicleAccess;
        stay.HostLivesHere = request.HostLivesHere;
        stay.Facilities = request.Facilities?.Distinct().ToArray() ?? Array.Empty<string>();
        stay.Description = request.Description;
        stay.RegistrationTypeId = regType.Id;
        stay.RegistrationTypeName = regType.Name;
        stay.RegistrationOtherName = otherName;
        if (numberNew != null) stay.RegistrationNumber = numberNew;
        stay.Gstin = gstin;
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
            return OkResponse(await ToHubDtoAsync(stay, unitOfWork, hub));
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

        return OkResponse(await ToHubDtoAsync(stay, unitOfWork, hub));
    }

    // Approved stay (contract 3.5, AU-5): stay type, vehicle access, host lives here, facilities and description save at once;
    // the review fields go into one pending change (only the changed ones) and stay live until the admin approves.
    private static async Task<IResult> SaveLiveBasicAsync(
        StayListing stay, UpsertStayBasicRequest request, StayType stayType, StayReviewValues asked, DateTime now,
        IUnitOfWork unitOfWork, IOwnerNotifier notifier, StayHubReader hub, IServiceProvider sp)
    {
        stay.StayTypeId = stayType.Id;
        stay.StayType = stayType;
        stay.VehicleAccess = request.VehicleAccess;
        stay.HostLivesHere = request.HostLivesHere;
        stay.Facilities = request.Facilities?.Distinct().ToArray() ?? Array.Empty<string>();
        stay.Description = request.Description;
        stay.UpdatedAt = now;

        var live = new StayReviewValues(stay.Name, stay.RegistrationTypeId, stay.RegistrationTypeName, stay.RegistrationOtherName,
            stay.RegistrationNumber, stay.Gstin, stay.Latitude, stay.Longitude, stay.Address, stay.DistrictId, stay.District?.Name,
            stay.CityId, stay.City?.Name);
        var current = StayPendingEditJson.Read(stay.PendingEditJson);
        var outcome = StayReviewFields.Decide(live, asked, current);
        if (outcome == StayReviewFields.SaveOutcome.ClearPending)
        {
            stay.PendingEditJson = null;
            stay.PendingEditAt = null;
        }
        else if (outcome == StayReviewFields.SaveOutcome.NewPending)
        {
            stay.PendingEditJson = StayPendingEditJson.Write(new StayPendingEdit(StayReviewFields.Diff(live, asked), asked));
            stay.PendingEditAt = now;
            stay.PendingEditResult = null;
            stay.PendingEditRejectReason = null;
            stay.PendingEditResultAt = null;
        }

        await unitOfWork.BeginTransactionAsync();
        try
        {
            await unitOfWork.SaveChangesAsync();
            if (outcome == StayReviewFields.SaveOutcome.NewPending)
                await notifier.ClearAsync(NoticeKinds.StayEditRejected, NoticeKinds.NoTrip, [stay.UserId]);
            await unitOfWork.CommitTransactionAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            await unitOfWork.RollbackTransactionAsync();
            return Concurrent();
        }
        catch
        {
            await unitOfWork.RollbackTransactionAsync();
            throw;
        }

        await StayCache.InvalidateAsync(sp.GetService<IConnectionMultiplexer>(), stay.DistrictId);
        return OkResponse(await ToHubDtoAsync(stay, unitOfWork, hub));
    }

    // Plan key Photos (contract 3.2, AU-9): refused when the visible photos reach the plan value or all rows (hidden too) reach
    // the ceiling of 12. Counted again under the stay row lock after the upload, so two phones at the limit get one 201 and one
    // 409 (whose file is deleted, best effort).
    public static async Task<IResult> UploadPhoto(
        IFormFile photo, ClaimsPrincipal principal, IUnitOfWork unitOfWork, ApplicationDbContext db, IPhotoService photoService,
        IPlanEntitlementService entitlements, IServiceProvider sp)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var stay = await unitOfWork.Stays.GetLiveByUserAsync(userId);
        if (stay == null) return NotFoundResponse(StayNotFound);
        if (!IsLiveEditable(stay)) return LockedResponse(stay);
        var limits = await entitlements.GetLimitsAsync(userId, ListingKinds.Stay);
        if (stay.Photos.Count >= limits.Photos)
            return OwnerHttp.LimitReached(OwnerErrorTypes.PlanPhotoLimit, PlanFeatureKeys.Photos, stay.Photos.Count, limits);
        if (PhotoUpload.Check(photo) is { } bad) return bad;

        using var stream = photo.OpenReadStream();
        // Shared Cloudinary prefix (user_{userId}/listing_{id}), so the shared
        // purge/account-deletion photo cleanup covers stays without changes.
        var (url, filePath) = await photoService.SavePhotoAsync(stream, photo.FileName, userId, stay.Id);

        StayPhoto? stayPhoto = null;
        IResult? refused = null;
        await unitOfWork.BeginTransactionAsync();
        try
        {
            if (!await OwnerLocks.LockStayAsync(db, stay.Id)) refused = NotFoundResponse(StayNotFound);
            else
            {
                // FB-38: the plan value is read again under the stay lock (a plan write takes the same lock after its own).
                limits = await entitlements.GetLimitsAsync(userId, ListingKinds.Stay);
                var counts = (await db.Database.SqlQueryRaw<PhotoCountRow>("""
                    SELECT (count(*) FILTER (WHERE NOT "HiddenByPlan"))::int AS "Visible", count(*)::int AS "All",
                           COALESCE(max("PhotoOrder"), -1) AS "MaxOrder"
                    FROM "StayPhotos" WHERE "StayListingId" = {0}
                    """, stay.Id).ToListAsync())[0];
                if (counts.All >= OwnerLimits.PhotosCeiling)
                    refused = OwnerHttp.LimitReached(OwnerErrorTypes.PlanPhotoLimit, PlanFeatureKeys.Photos, OwnerLimits.PhotosCeiling, counts.All, false);
                else if (!PlanRowChoice.PhotoUploadAllowed(counts.Visible, counts.All, limits.Photos))
                    refused = OwnerHttp.LimitReached(OwnerErrorTypes.PlanPhotoLimit, PlanFeatureKeys.Photos, counts.Visible, limits);
                else
                {
                    stayPhoto = new StayPhoto
                    {
                        Id = Guid.NewGuid(),
                        StayListingId = stay.Id,
                        PhotoUrl = url,
                        FilePath = filePath,
                        PhotoOrder = counts.MaxOrder + 1,
                        UploadedAt = DateTime.UtcNow,
                    };
                    db.StayPhotos.Add(stayPhoto);
                    stay.UpdatedAt = DateTime.UtcNow;
                    await unitOfWork.SaveChangesAsync();
                }
            }
            if (refused != null) await unitOfWork.RollbackTransactionAsync();
            else await unitOfWork.CommitTransactionAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            await unitOfWork.RollbackTransactionAsync();
            refused = Concurrent();
        }
        catch
        {
            await unitOfWork.RollbackTransactionAsync();
            await PhotoCleanup.TryDeleteAsync(photoService, filePath, PhotoCleanup.Logger(sp));
            throw;
        }
        if (refused != null || stayPhoto == null)
        {
            await PhotoCleanup.TryDeleteAsync(photoService, filePath, PhotoCleanup.Logger(sp));
            return refused ?? Concurrent();
        }

        if (stay.Status == StayStatuses.Approved)
            await StayCache.InvalidateAsync(sp.GetService<IConnectionMultiplexer>(), stay.DistrictId);
        return CreatedResponse(new { photoUrl = url, photoId = stayPhoto.Id }, url);
    }

    // Live edit: a live stay keeps at least one visible photo (409 StayNeedsPhoto). FB-54: the delete runs under the owner lock and
    // the stay lock with the "another visible photo" count in the same statement, so two parallel deletes cannot both pass. FB-19
    // (D-28): a freed visible slot is refilled from the hidden photos in the same transaction (limits re-read under the lock).
    // Lock order: Users -> StayListings -> StayPhotos. The file goes after the row commit.
    public static async Task<IResult> DeletePhoto(
        Guid photoId, ClaimsPrincipal principal, IUnitOfWork unitOfWork, ApplicationDbContext db, IPhotoService photoService,
        ILiveSlotService slots, IPlanEntitlementService entitlements, PlanLimitsReconcile planRows, IServiceProvider sp)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var stay = await unitOfWork.Stays.GetLiveByUserAsync(userId);
        if (stay == null) return NotFoundResponse(StayNotFound);
        if (!IsLiveEditable(stay)) return LockedResponse(stay);

        var photo = stay.Photos.FirstOrDefault(p => p.Id == photoId);
        if (photo == null) return NotFoundResponse("Photo not found");

        IResult? refused = null;
        await unitOfWork.BeginTransactionAsync();
        try
        {
            await slots.LockOwnerAsync(userId);
            if (!await OwnerLocks.LockStayAsync(db, stay.Id)) refused = NotFoundResponse(StayNotFound);
            else
            {
                var deleted = await db.Database.ExecuteSqlRawAsync(DeletePhotoSql,
                    new NpgsqlParameter("p", NpgsqlDbType.Uuid) { Value = photoId },
                    new NpgsqlParameter("s", NpgsqlDbType.Uuid) { Value = stay.Id },
                    new NpgsqlParameter("approved", NpgsqlDbType.Varchar) { Value = StayStatuses.Approved });
                if (deleted == 0)
                    refused = await db.StayPhotos.AnyAsync(p => p.Id == photoId && p.StayListingId == stay.Id)
                        ? OwnerHttp.Refuse(OwnerErrorTypes.StayNeedsPhoto)
                        : NotFoundResponse("Photo not found");
                else
                {
                    var limits = await entitlements.GetLimitsAsync(userId, ListingKinds.Stay);
                    var effect = await planRows.ApplyForOwnerAsync(userId, ListingKinds.Stay, limits);
                    if (effect.HiddenBefore > 0 && effect.HiddenAfter == 0) await planRows.ClearLimitsNoticeAsync(userId);
                    await db.Database.ExecuteSqlRawAsync("""UPDATE "StayListings" SET "UpdatedAt" = now() WHERE "Id" = @s""",
                        new NpgsqlParameter("s", NpgsqlDbType.Uuid) { Value = stay.Id });
                }
            }
            if (refused != null) await unitOfWork.RollbackTransactionAsync();
            else await unitOfWork.CommitTransactionAsync();
        }
        catch
        {
            await unitOfWork.RollbackTransactionAsync();
            throw;
        }
        if (refused != null) return refused;

        await PhotoCleanup.TryDeleteAsync(photoService, photo.FilePath, PhotoCleanup.Logger(sp));
        if (stay.Status == StayStatuses.Approved)
            await StayCache.InvalidateAsync(sp.GetService<IConnectionMultiplexer>(), stay.DistrictId);
        return NoContentResponse();
    }

    // Swaps one visible photo in place: same Id, PhotoOrder and HiddenByPlan, so no plan count and no owner lock. Lock order
    // StayListings -> StayPhotos. The new file goes on any refusal or error; the old file goes after the commit.
    public static async Task<IResult> ReplacePhoto(
        Guid photoId, IFormFile photo, ClaimsPrincipal principal, IUnitOfWork unitOfWork, ApplicationDbContext db,
        IPhotoService photoService, IServiceProvider sp)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var stay = await db.StayListings.AsNoTracking()
            .Where(s => s.UserId == userId && !s.IsDeleted)
            .Select(s => new { s.Id, s.Status, s.DistrictId })
            .FirstOrDefaultAsync();
        if (stay == null) return NotFoundResponse(StayNotFound);
        if (stay.Status == StayStatuses.PendingReview) return LockedResponse(stay.Status);
        if (PhotoUpload.Check(photo) is { } bad) return bad;

        using var stream = photo.OpenReadStream();
        var logger = PhotoCleanup.Logger(sp);
        var (url, filePath) = await photoService.SavePhotoAsync(stream, photo.FileName, userId, stay.Id);

        string? oldPath = null;
        var status = stay.Status;
        IResult? refused = null;
        try
        {
            await unitOfWork.BeginTransactionAsync();
            if (!await OwnerLocks.LockStayAsync(db, stay.Id)) refused = NotFoundResponse(StayNotFound);
            else
            {
                var old = await db.Database.SqlQueryRaw<PhotoSwapRow>(ReplacePhotoSql,
                    new NpgsqlParameter("p", NpgsqlDbType.Uuid) { Value = photoId },
                    new NpgsqlParameter("s", NpgsqlDbType.Uuid) { Value = stay.Id },
                    new NpgsqlParameter("user", NpgsqlDbType.Uuid) { Value = userId },
                    new NpgsqlParameter("review", NpgsqlDbType.Varchar) { Value = StayStatuses.PendingReview },
                    new NpgsqlParameter("url", NpgsqlDbType.Varchar) { Value = url },
                    new NpgsqlParameter("path", NpgsqlDbType.Varchar) { Value = filePath }).ToListAsync();
                if (old.Count == 0)
                {
                    // Submit may have committed during the upload: under review answers 409, anything else 404.
                    var current = await db.StayListings.AsNoTracking().Where(s => s.Id == stay.Id).Select(s => s.Status).FirstOrDefaultAsync();
                    refused = current == StayStatuses.PendingReview ? LockedResponse(current) : NotFoundResponse("Photo not found");
                }
                else
                {
                    oldPath = old[0].OldPath;
                    status = old[0].Status;
                    await db.Database.ExecuteSqlRawAsync("""UPDATE "StayListings" SET "UpdatedAt" = now() WHERE "Id" = @s""",
                        new NpgsqlParameter("s", NpgsqlDbType.Uuid) { Value = stay.Id });
                }
            }
            if (refused != null) await unitOfWork.RollbackTransactionAsync();
            else await unitOfWork.CommitTransactionAsync();
        }
        catch
        {
            await PhotoCleanup.TryDeleteAsync(photoService, filePath, logger);
            try { await unitOfWork.RollbackTransactionAsync(); } catch { /* the original error is rethrown */ }
            throw;
        }
        if (refused != null)
        {
            await PhotoCleanup.TryDeleteAsync(photoService, filePath, logger);
            return refused;
        }

        if (oldPath != filePath) await PhotoCleanup.TryDeleteAsync(photoService, oldPath, logger);
        if (status == StayStatuses.Approved)
            await StayCache.InvalidateAsync(sp.GetService<IConnectionMultiplexer>(), stay.DistrictId);
        return OkResponse(new { photoId, photoUrl = url });
    }

    // Keeps "Id", "PhotoOrder" and "HiddenByPlan"; a photo hidden by the plan, a foreign stay or a stay under review is not
    // replaceable (0 rows). Ownership and the review lock sit in the write statement itself.
    public const string ReplacePhotoSql = """
        WITH old AS (
            SELECT p."Id", p."FilePath", s."Status" FROM "StayPhotos" p
            JOIN "StayListings" s ON s."Id" = p."StayListingId"
            WHERE p."Id" = @p AND p."StayListingId" = @s AND NOT p."HiddenByPlan"
              AND s."UserId" = @user AND NOT s."IsDeleted" AND s."Status" <> @review
            FOR UPDATE OF p
        )
        UPDATE "StayPhotos" p
        SET "PhotoUrl" = @url, "FilePath" = @path, "UploadedAt" = now()
        FROM old
        WHERE p."Id" = old."Id"
        RETURNING old."FilePath" AS "OldPath", old."Status" AS "Status"
        """;

    private sealed class PhotoSwapRow
    {
        public string? OldPath { get; set; }
        public string Status { get; set; } = string.Empty;
    }

    // One statement: a visible photo of this stay, and (live stay) at least one other visible photo stays. Hidden photos are not
    // deletable here (the hub shows them in "Choose what stays").
    public const string DeletePhotoSql = """
        DELETE FROM "StayPhotos" p
        WHERE p."Id" = @p AND p."StayListingId" = @s AND NOT p."HiddenByPlan"
          AND ((SELECT l."Status" FROM "StayListings" l WHERE l."Id" = @s) <> @approved
               OR EXISTS (SELECT 1 FROM "StayPhotos" o WHERE o."StayListingId" = @s AND o."Id" <> @p AND NOT o."HiddenByPlan"))
        """;

    // Plan key RoomTypes (contract 3.2): a save that adds above the plan value is refused (409 PlanRoomTypeLimit); keeping or
    // shrinking an over-limit set passes. Above the ceiling of 8 the validator answers 400 StayRoomLimit. Room names are unique
    // across visible and hidden rooms (AU-9). Rows hidden by the plan are never loaded here, so the full replace keeps them.
    public static async Task<IResult> UpsertRooms(
        UpsertStayRoomsRequest request,
        ClaimsPrincipal principal,
        IValidator<UpsertStayRoomsRequest> validator,
        IUnitOfWork unitOfWork,
        ApplicationDbContext db,
        IPlanEntitlementService entitlements,
        StayHubReader hub,
        ILiveSlotService slots,
        PlanLimitsReconcile planRows,
        IServiceProvider sp)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            var error = validation.Errors[0];
            if (error.ErrorCode == OwnerErrorTypes.RoomTypeDuplicate)
                return BadRequestResponse(error.ErrorMessage, OwnerErrorTypes.RoomTypeDuplicate);
            return error.PropertyName == nameof(UpsertStayRoomsRequest.Rooms)
                ? BadRequestResponse(error.ErrorMessage, StayErrorTypes.StayRoomLimit)
                : BadRequestResponse(error.ErrorMessage);
        }

        var stay = await unitOfWork.Stays.GetLiveByUserAsync(userId);
        if (stay == null) return NotFoundResponse(StayNotFound);
        if (!IsLiveEditable(stay)) return LockedResponse(stay);
        if (!StaySetupRules.Step1Done(stay)) return StepLocked(1);

        // Fast refusal outside the lock; the same check runs again under the locks below (FB-38).
        var limits = await entitlements.GetLimitsAsync(userId, ListingKinds.Stay);
        if (!PlanRowChoice.SaveAllowed(request.Rooms.Count, limits.RoomTypes, stay.Rooms.Count))
            return OwnerHttp.LimitReached(OwnerErrorTypes.PlanRoomTypeLimit, PlanFeatureKeys.RoomTypes, stay.Rooms.Count, limits);
        var hiddenNames = await db.StayRooms.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.StayListingId == stay.Id && r.HiddenByPlan).Select(r => r.RoomName).ToListAsync();
        if (request.Rooms.FirstOrDefault(r => hiddenNames.Contains(r.RoomName)) is { } clash)
            return BadRequestResponse(OwnerErrorTypes.RoomTypeDuplicateMessage(clash.RoomName), OwnerErrorTypes.RoomTypeDuplicate);

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
            if (input.Floor != null) room.Floor = input.Floor;
            room.UpdatedAt = now;
        }
        foreach (var extra in existing.Where(r => r.RoomIndex > request.Rooms.Count))
        {
            db.StayRooms.Remove(extra);
            stay.Rooms.Remove(extra);
        }

        RecomputeMinPrice(stay);
        stay.UpdatedAt = now;
        var removedVisible = existing.Count > request.Rooms.Count;

        // FB-38: limits re-read under the owner and stay locks (lock order Users -> StayListings), so a downgrade that commits
        // between the first read and the save cannot let a save above the new value through. FB-19 (D-28): a removed visible
        // room frees a slot, which the next hidden room fills in the same transaction.
        IResult? refused = null;
        var refilled = false;
        await unitOfWork.BeginTransactionAsync();
        try
        {
            await slots.LockOwnerAsync(userId);
            if (!await OwnerLocks.LockStayAsync(db, stay.Id)) refused = NotFoundResponse(StayNotFound);
            else
            {
                limits = await entitlements.GetLimitsAsync(userId, ListingKinds.Stay);
                if (!PlanRowChoice.SaveAllowed(request.Rooms.Count, limits.RoomTypes, existing.Count))
                    refused = OwnerHttp.LimitReached(OwnerErrorTypes.PlanRoomTypeLimit, PlanFeatureKeys.RoomTypes, existing.Count, limits);
                else
                {
                    await unitOfWork.SaveChangesAsync();
                    if (removedVisible)
                    {
                        var effect = await planRows.ApplyForOwnerAsync(userId, ListingKinds.Stay, limits);
                        if (effect.HiddenBefore > 0 && effect.HiddenAfter == 0) await planRows.ClearLimitsNoticeAsync(userId);
                        refilled = effect.Changed;
                    }
                }
            }
            if (refused != null) await unitOfWork.RollbackTransactionAsync();
            else await unitOfWork.CommitTransactionAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            await unitOfWork.RollbackTransactionAsync();
            return Concurrent();
        }
        catch
        {
            await unitOfWork.RollbackTransactionAsync();
            throw;
        }
        if (refused != null) return refused;
        if (refilled)
        {
            // The refill wrote rows by statement; the answer reads the stay again instead of the stale tracked graph.
            db.ChangeTracker.Clear();
            stay = await unitOfWork.Stays.GetLiveByUserAsync(userId) ?? stay;
        }

        if (stay.Status == StayStatuses.Approved)
            await StayCache.InvalidateAsync(sp.GetService<IConnectionMultiplexer>(), stay.DistrictId);
        return OkResponse(await ToHubDtoAsync(stay, unitOfWork, hub, limits));
    }

    public static async Task<IResult> UpsertRules(
        UpsertStayRulesRequest request,
        ClaimsPrincipal principal,
        IValidator<UpsertStayRulesRequest> validator,
        IUnitOfWork unitOfWork,
        StayHubReader hub,
        IServiceProvider sp)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
            return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var stay = await unitOfWork.Stays.GetLiveByUserAsync(userId);
        if (stay == null) return NotFoundResponse(StayNotFound);
        if (!IsLiveEditable(stay)) return LockedResponse(stay);
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

        if (stay.Status == StayStatuses.Approved)
            await StayCache.InvalidateAsync(sp.GetService<IConnectionMultiplexer>(), stay.DistrictId);
        return OkResponse(await ToHubDtoAsync(stay, unitOfWork, hub));
    }

    public static async Task<IResult> Submit(
        ClaimsPrincipal principal, IUnitOfWork unitOfWork, ApplicationDbContext db, IRabbitMqPublisher publisher, StayHubReader hub)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var stay = await unitOfWork.Stays.GetLiveByUserAsync(userId);
        if (stay == null) return NotFoundResponse(StayNotFound);
        if (!IsEditable(stay)) return LockedResponse(stay);
        if (!StaySetupRules.Step1Done(stay)) return StepLocked(1);
        if (!StaySetupRules.RoomsDone(stay)) return StepLocked(2);
        if (!StaySetupRules.RulesDone(stay)) return StepLocked(3);

        var check = BusinessOnboardingRules.NeedsFlagCheckAtSubmit(stay.EverApproved)
            ? await BusinessOnboardingGate.CheckAsync(db, stay.DistrictId!.Value, stay.Latitude!.Value, stay.Longitude!.Value)
            : OnboardingCheck.Ok;
        if (BusinessOnboardingRules.IsOutsideDistrict(check))
            return BadRequestResponse("Pin your stay inside the selected district", StayErrorTypes.StayOutsideDistrict);
        if (check == OnboardingCheck.OnboardingClosed)
            return BadRequestResponse(BusinessOnboardingRules.OnboardingClosedMessage, BusinessOnboardingRules.OnboardingClosedType);

        // Snapshot the type name as reviewed; a type renamed since the last basic save is refreshed here.
        var regType = await unitOfWork.StayRegistrationTypes.GetByIdAsync(stay.RegistrationTypeId);
        if (regType != null) stay.RegistrationTypeName = regType.Name;

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

        return OkResponse(await ToHubDtoAsync(stay, unitOfWork, hub));
    }

    public static async Task<IResult> UpdateAccepting(
        UpdateStayAcceptingRequest request,
        ClaimsPrincipal principal,
        IValidator<UpdateStayAcceptingRequest> validator,
        IUnitOfWork unitOfWork,
        StayHubReader hub,
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

        // FB-11: the same value is a no-op (no save, no cache bust).
        if (stay.AcceptingGuests == request.AcceptingGuests!.Value) return OkResponse(await ToHubDtoAsync(stay, unitOfWork, hub));
        stay.AcceptingGuests = request.AcceptingGuests.Value;
        stay.UpdatedAt = DateTime.UtcNow;

        try { await unitOfWork.SaveChangesAsync(); }
        catch (DbUpdateConcurrencyException) { return Concurrent(); }

        await StayCache.InvalidateAsync(sp.GetService<IConnectionMultiplexer>(), stay.DistrictId);
        return OkResponse(await ToHubDtoAsync(stay, unitOfWork, hub));
    }

    // PUT /stays/me/share-details: null keeps a value, "" clears it. Never touches the status or the public caches.
    public static async Task<IResult> UpdateShareDetails(
        UpdateStayShareDetailsRequest request,
        ClaimsPrincipal principal,
        IValidator<UpdateStayShareDetailsRequest> validator,
        IUnitOfWork unitOfWork,
        StayHubReader hub)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
            return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var stay = await unitOfWork.Stays.GetLiveByUserAsync(userId);
        if (stay == null) return NotFoundResponse(StayNotFound);

        if (request.UpiId != null)
            stay.UpiId = request.UpiId.Trim().Length == 0 ? null : request.UpiId.Trim();
        if (request.WhatsAppNumber != null)
            stay.WhatsAppNumber = request.WhatsAppNumber.Trim().Length == 0 ? null : request.WhatsAppNumber.Trim();
        stay.UpdatedAt = DateTime.UtcNow;

        try { await unitOfWork.SaveChangesAsync(); }
        catch (DbUpdateConcurrencyException) { return Concurrent(); }

        return OkResponse(await ToHubDtoAsync(stay, unitOfWork, hub));
    }

    public static async Task<IResult> UpdateRoomPrice(
        Guid roomId,
        UpdateStayRoomPriceRequest request,
        ClaimsPrincipal principal,
        IValidator<UpdateStayRoomPriceRequest> validator,
        IUnitOfWork unitOfWork,
        StayHubReader hub,
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
        return OkResponse(await ToHubDtoAsync(stay, unitOfWork, hub));
    }

    // Soft delete (contract 6.2): the owner tables of the stay go through the purge seam in the same transaction (the soft delete
    // keeps the listing row, so nothing cascades), a waiting change is dropped, and the answer counts the confirmed bookings the
    // closure cancelled. 200 with a body (old builds ignore it).
    public static async Task<IResult> DeleteMine(
        ClaimsPrincipal principal, IUnitOfWork unitOfWork, IListingPurgeService listingPurge, IServiceProvider sp,
        ITripEnquiryClosure tripClosure, ApplicationDbContext db, IOwnerListingPurge ownerPurge)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var stay = await unitOfWork.Stays.GetLiveByUserAsync(userId);
        if (stay == null) return NotFoundResponse(StayNotFound);

        var removed = await ListingRemoval.SoftDeleteStayAsync(stay, unitOfWork, db, tripClosure, ownerPurge, listingPurge, sp);
        return removed.Concurrent ? Concurrent() : OkResponse(new { cancelledBookings = removed.CancelledBookings });
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
}
