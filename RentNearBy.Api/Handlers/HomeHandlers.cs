using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using StackExchange.Redis;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

public static class HomeHandlers
{
    private static readonly TimeSpan RecentCacheTtl = TimeSpan.FromMinutes(3);
    // Shorter than the two above — "for you" is actively invalidated on mutation same as they
    // are (see ListingsHandlers/PlotHandlers/GoLiveHandlers/AdminHandlers), but a shorter TTL
    // bounds staleness for any write path that doesn't go through this backend at all (there
    // isn't one today, but it's the same 60s figure this codebase already uses for the nearby
    // cache — NearbyCacheTtl in ListingsHandlers.cs/PlotHandlers.cs — not a new number invented
    // for this feature).
    private static readonly TimeSpan ForYouCacheTtl = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan BrowseCacheTtl = TimeSpan.FromSeconds(30);
    private const int DefaultLimit = 5;
    private const int MaxLimit = 20;
    private const int DefaultPageSize = 10;
    private const int MaxPageSize = 30;

    private static readonly HashSet<string> RoomSortValues = new() { "newest", "price_asc", "price_desc" };
    private static readonly HashSet<string> PlotSortValues = new() { "newest", "area_asc", "area_desc" };

    // No districtId in the key — this feed is intentionally district-free, identical for
    // every caller, which is exactly what makes it a good caching candidate in the first place.
    private const string RecentRoomsCacheKey = "home:recentRooms";
    private const string RecentPlotsCacheKey = "home:recentPlots";
    private static string ForYouRoomsCacheKey(Guid districtId) => $"home:forYouRooms:{districtId}";
    private static string ForYouPlotsCacheKey(Guid districtId) => $"home:forYouPlots:{districtId}";

    // First browse page only (no cursor, page <= 1): identical for every caller with the same
    // district/city/type/sort/pageSize. Pages after the first are never cached.
    private static string BrowseRoomsCacheKey(Guid districtId, Guid? cityId, Guid? typeId, string sort, int pageSize)
        => $"home:browseRooms:{districtId:N}:{cityId:N}:{typeId:N}:{sort}:{pageSize}";
    private static string BrowsePlotsCacheKey(Guid districtId, Guid? cityId, Guid? typeId, string sort, int pageSize)
        => $"home:browsePlots:{districtId:N}:{cityId:N}:{typeId:N}:{sort}:{pageSize}";

    private static async Task<T?> ReadBrowseCacheAsync<T>(IConnectionMultiplexer? redis, string key) where T : class
    {
        if (redis == null) return null;
        try
        {
            var cached = await redis.GetDatabase().StringGetAsync(key);
            return cached.HasValue ? JsonSerializer.Deserialize<T>(cached!) : null;
        }
        catch { return null; }
    }

    private static async Task WriteBrowseCacheAsync<T>(IConnectionMultiplexer? redis, string key, T value)
    {
        if (redis == null) return;
        try { await redis.GetDatabase().StringSetAsync(key, JsonSerializer.Serialize(value), BrowseCacheTtl); } catch { }
    }

    private static int ClampLimit(int limit) => Math.Clamp(limit <= 0 ? DefaultLimit : limit, 1, MaxLimit);
    private static int ClampPage(int page) => Math.Max(page, 1);
    private static int ClampPageSize(int pageSize) => Math.Clamp(pageSize <= 0 ? DefaultPageSize : pageSize, 1, MaxPageSize);
    private static string ValidateRoomSort(string? sortBy) => RoomSortValues.Contains(sortBy ?? "") ? sortBy! : "newest";
    private static string ValidatePlotSort(string? sortBy) => PlotSortValues.Contains(sortBy ?? "") ? sortBy! : "newest";

    // Home's "for you"/"recently added" preview sections deliberately degrade to an EMPTY list (not
    // a 403/error) when the Rooms or Plots tab is deactivated — the section itself is expected to
    // hide client-side, so an error here would just be a wasted toast the user never sees. Contrast
    // with TabGateFilter, which hard-rejects the vertical's own dedicated route group.
    private static async Task<bool> IsRoomsTabInactiveAsync(IUnitOfWork unitOfWork, IMemoryCache cache)
        => !await ConfigHandlers.IsTabActiveCachedAsync(AppTabKeys.Rooms, unitOfWork, cache);

    private static async Task<bool> IsPlotsTabInactiveAsync(IUnitOfWork unitOfWork, IMemoryCache cache)
        => !await ConfigHandlers.IsTabActiveCachedAsync(AppTabKeys.Plots, unitOfWork, cache);

    public static async Task<IResult> GetRooms(Guid districtId, int limit, IUnitOfWork unitOfWork, IMemoryCache cache, IServiceProvider sp)
    {
        if (await IsRoomsTabInactiveAsync(unitOfWork, cache)) return OkResponse(new { items = Array.Empty<HomeRoomDto>() });
        var take = ClampLimit(limit);
        var redis = sp.GetService<IConnectionMultiplexer>();
        var cacheKey = ForYouRoomsCacheKey(districtId);

        if (redis != null)
        {
            RedisValue cached = default;
            try { cached = await redis.GetDatabase().StringGetAsync(cacheKey); } catch { }
            if (cached.HasValue)
            {
                try
                {
                    var dto = JsonSerializer.Deserialize<List<HomeRoomDto>>(cached!);
                    if (dto != null) return OkResponse(new { items = dto.Take(take) });
                }
                catch (JsonException) { /* corrupted cache entry — fall through to DB */ }
            }
        }

        var items = await unitOfWork.RoomListings.SearchAsync(districtId, null, null, null, MaxLimit);
        var result = items.Select(l => new HomeRoomDto
        {
            Id = l.Id,
            UserId = l.UserId,
            PriceMonthly = l.PriceMonthly,
            RoomTypeName = l.RoomType?.Name,
            ThumbnailUrl = l.Photos.FirstOrDefault()?.PhotoUrl,
            CityName = l.City?.Name,
            DistrictName = l.District?.Name ?? string.Empty,
            FurnishedStatus = l.FurnishedStatus,
            CreatedAt = l.CreatedAt,
        }).ToList();

        if (redis != null)
        {
            var json = JsonSerializer.Serialize(result);
            try { await redis.GetDatabase().StringSetAsync(cacheKey, json, ForYouCacheTtl); } catch { }
        }

        return OkResponse(new { items = result.Take(take) });
    }

    public static async Task<IResult> GetPlots(Guid districtId, int limit, IUnitOfWork unitOfWork, IMemoryCache cache, IServiceProvider sp)
    {
        if (await IsPlotsTabInactiveAsync(unitOfWork, cache)) return OkResponse(new { items = Array.Empty<HomePlotDto>() });
        var take = ClampLimit(limit);
        var redis = sp.GetService<IConnectionMultiplexer>();
        var cacheKey = ForYouPlotsCacheKey(districtId);

        if (redis != null)
        {
            RedisValue cached = default;
            try { cached = await redis.GetDatabase().StringGetAsync(cacheKey); } catch { }
            if (cached.HasValue)
            {
                try
                {
                    var dto = JsonSerializer.Deserialize<List<HomePlotDto>>(cached!);
                    if (dto != null) return OkResponse(new { items = dto.Take(take) });
                }
                catch (JsonException) { /* corrupted cache entry — fall through to DB */ }
            }
        }

        var (items, _) = await unitOfWork.PlotListings.GetAllAsync(page: 1, pageSize: MaxLimit, isActive: true, districtId: districtId);
        var result = items.Select(p => new HomePlotDto
        {
            Id = p.Id,
            UserId = p.UserId,
            AreaValue = p.AreaValue,
            AreaUnit = p.AreaUnit,
            PlotTypeName = p.PlotType?.Name,
            ThumbnailUrl = p.Photos.FirstOrDefault()?.PhotoUrl,
            CityName = p.City?.Name,
            DistrictName = p.District?.Name ?? string.Empty,
            CreatedAt = p.CreatedAt,
        }).ToList();

        if (redis != null)
        {
            var json = JsonSerializer.Serialize(result);
            try { await redis.GetDatabase().StringSetAsync(cacheKey, json, ForYouCacheTtl); } catch { }
        }

        return OkResponse(new { items = result.Take(take) });
    }

    public static async Task<IResult> GetRoomsBrowse(
        Guid districtId, Guid? cityId, Guid? roomTypeId, string? sortBy, int page, int pageSize, string? cursor,
        IUnitOfWork unitOfWork, IMemoryCache cache, IServiceProvider sp)
    {
        if (await IsRoomsTabInactiveAsync(unitOfWork, cache))
            return OkResponse(new { items = Array.Empty<HomeRoomDto>(), hasMore = false, nextCursor = (string?)null });

        var sort = ValidateRoomSort(sortBy);
        var size = ClampPageSize(pageSize);
        var pageNo = ClampPage(page);
        var firstPage = string.IsNullOrEmpty(cursor) && pageNo <= 1;
        var redis = firstPage ? sp.GetService<IConnectionMultiplexer>() : null;
        var cacheKey = BrowseRoomsCacheKey(districtId, cityId, roomTypeId, sort, size);

        var result = await ReadBrowseCacheAsync<BrowsePage<HomeRoomDto>>(redis, cacheKey);
        if (result == null)
        {
            result = await unitOfWork.RoomListings.SearchPagedAsync(districtId, cityId, roomTypeId, sort, pageNo, size, cursor);
            await WriteBrowseCacheAsync(redis, cacheKey, result);
        }

        return OkResponse(new { items = result.Items, hasMore = result.HasMore, nextCursor = result.NextCursor });
    }

    public static async Task<IResult> GetPlotsBrowse(
        Guid districtId, Guid? cityId, Guid? plotTypeId, string? sortBy, int page, int pageSize, string? cursor,
        IUnitOfWork unitOfWork, IMemoryCache cache, IServiceProvider sp)
    {
        if (await IsPlotsTabInactiveAsync(unitOfWork, cache))
            return OkResponse(new { items = Array.Empty<HomePlotDto>(), hasMore = false, nextCursor = (string?)null });

        var sort = ValidatePlotSort(sortBy);
        var size = ClampPageSize(pageSize);
        var pageNo = ClampPage(page);
        var firstPage = string.IsNullOrEmpty(cursor) && pageNo <= 1;
        var redis = firstPage ? sp.GetService<IConnectionMultiplexer>() : null;
        var cacheKey = BrowsePlotsCacheKey(districtId, cityId, plotTypeId, sort, size);

        var result = await ReadBrowseCacheAsync<BrowsePage<HomePlotDto>>(redis, cacheKey);
        if (result == null)
        {
            result = await unitOfWork.PlotListings.GetAllPagedByTypeIdAsync(districtId, cityId, plotTypeId, sort, pageNo, size, cursor);
            await WriteBrowseCacheAsync(redis, cacheKey, result);
        }

        return OkResponse(new { items = result.Items, hasMore = result.HasMore, nextCursor = result.NextCursor });
    }

    // Deliberately separate from GetRooms/GetRoomsBrowse rather than an optional-districtId
    // overload of either: this is a structurally different query (no district/city locality
    // to filter or rank by) and, being identical for every caller, is cached — GetRooms/
    // GetRoomsBrowse are per-district and were never worth caching the same way.
    public static async Task<IResult> GetRecentRooms(int limit, IUnitOfWork unitOfWork, IMemoryCache cache, IServiceProvider sp)
    {
        if (await IsRoomsTabInactiveAsync(unitOfWork, cache)) return OkResponse(new { items = Array.Empty<HomeRoomDto>() });
        var take = ClampLimit(limit);
        var redis = sp.GetService<IConnectionMultiplexer>();

        if (redis != null)
        {
            RedisValue cached = default;
            try { cached = await redis.GetDatabase().StringGetAsync(RecentRoomsCacheKey); } catch { }
            if (cached.HasValue)
            {
                try
                {
                    var dto = JsonSerializer.Deserialize<List<HomeRoomDto>>(cached!);
                    if (dto != null) return OkResponse(new { items = dto.Take(take) });
                }
                catch (JsonException) { /* corrupted cache entry — fall through to DB */ }
            }
        }

        var items = await unitOfWork.RoomListings.GetRecentAsync(MaxLimit);
        var result = items.Select(l => new HomeRoomDto
        {
            Id = l.Id,
            UserId = l.UserId,
            PriceMonthly = l.PriceMonthly,
            RoomTypeName = l.RoomType?.Name,
            ThumbnailUrl = l.Photos.FirstOrDefault()?.PhotoUrl,
            CityName = l.City?.Name,
            DistrictName = l.District?.Name ?? string.Empty,
            FurnishedStatus = l.FurnishedStatus,
            CreatedAt = l.CreatedAt,
        }).ToList();

        if (redis != null)
        {
            var json = JsonSerializer.Serialize(result);
            try { await redis.GetDatabase().StringSetAsync(RecentRoomsCacheKey, json, RecentCacheTtl); } catch { }
        }

        return OkResponse(new { items = result.Take(take) });
    }

    public static async Task<IResult> GetRecentPlots(int limit, IUnitOfWork unitOfWork, IMemoryCache cache, IServiceProvider sp)
    {
        if (await IsPlotsTabInactiveAsync(unitOfWork, cache)) return OkResponse(new { items = Array.Empty<HomePlotDto>() });
        var take = ClampLimit(limit);
        var redis = sp.GetService<IConnectionMultiplexer>();

        if (redis != null)
        {
            RedisValue cached = default;
            try { cached = await redis.GetDatabase().StringGetAsync(RecentPlotsCacheKey); } catch { }
            if (cached.HasValue)
            {
                try
                {
                    var dto = JsonSerializer.Deserialize<List<HomePlotDto>>(cached!);
                    if (dto != null) return OkResponse(new { items = dto.Take(take) });
                }
                catch (JsonException) { /* corrupted cache entry — fall through to DB */ }
            }
        }

        var items = await unitOfWork.PlotListings.GetRecentAsync(MaxLimit);
        var result = items.Select(p => new HomePlotDto
        {
            Id = p.Id,
            UserId = p.UserId,
            AreaValue = p.AreaValue,
            AreaUnit = p.AreaUnit,
            PlotTypeName = p.PlotType?.Name,
            ThumbnailUrl = p.Photos.FirstOrDefault()?.PhotoUrl,
            CityName = p.City?.Name,
            DistrictName = p.District?.Name ?? string.Empty,
            CreatedAt = p.CreatedAt,
        }).ToList();

        if (redis != null)
        {
            var json = JsonSerializer.Serialize(result);
            try { await redis.GetDatabase().StringSetAsync(RecentPlotsCacheKey, json, RecentCacheTtl); } catch { }
        }

        return OkResponse(new { items = result.Take(take) });
    }
}
