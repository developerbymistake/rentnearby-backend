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

    // Home's preview sections deliberately degrade to an EMPTY list (not a 403/error) when their tab is off —
    // the section hides client-side, so an error would just be a wasted toast. The soft-empty result is never
    // cached, so turning the tab back on is instant. Contrast with TabGateFilter on the vertical's own group.
    private static async Task<bool> IsTabInactiveAsync(string tabKey, IUnitOfWork unitOfWork, IMemoryCache cache)
        => !TabState.IsActive(await ConfigHandlers.GetAppTabsCachedAsync(unitOfWork, cache), tabKey);

    // Corrupted entry => miss (falls through to the DB).
    private static T? Deserialize<T>(RedisValue value) where T : class
    {
        if (!value.HasValue) return null;
        try { return JsonSerializer.Deserialize<T>(value!); }
        catch (JsonException) { return null; }
    }

    // Read-through for one cached list: a hit skips the DB, a miss loads and writes back best-effort.
    private static async Task<List<T>> ReadThroughListAsync<T>(
        IConnectionMultiplexer? redis, string key, TimeSpan ttl, Func<Task<List<T>>> load)
    {
        if (redis != null)
        {
            RedisValue cached = default;
            try { cached = await redis.GetDatabase().StringGetAsync(key); } catch { }
            var hit = Deserialize<List<T>>(cached);
            if (hit != null) return hit;
        }

        var fresh = await load();
        if (redis != null)
        {
            try { await redis.GetDatabase().StringSetAsync(key, JsonSerializer.Serialize(fresh), ttl); } catch { }
        }
        return fresh;
    }

    public static async Task<IResult> GetRooms(Guid districtId, int limit, IUnitOfWork unitOfWork, IMemoryCache cache, IServiceProvider sp)
    {
        if (await IsTabInactiveAsync(AppTabKeys.Rooms, unitOfWork, cache)) return OkResponse(new { items = Array.Empty<HomeRoomDto>() });
        var items = await ReadThroughListAsync(sp.GetService<IConnectionMultiplexer>(), ForYouRoomsCacheKey(districtId), ForYouCacheTtl,
            () => unitOfWork.RoomListings.GetHomeForYouProjectedAsync(districtId, MaxLimit));
        return OkResponse(new { items = items.Take(ClampLimit(limit)) });
    }

    public static async Task<IResult> GetPlots(Guid districtId, int limit, IUnitOfWork unitOfWork, IMemoryCache cache, IServiceProvider sp)
    {
        if (await IsTabInactiveAsync(AppTabKeys.Plots, unitOfWork, cache)) return OkResponse(new { items = Array.Empty<HomePlotDto>() });
        var items = await ReadThroughListAsync(sp.GetService<IConnectionMultiplexer>(), ForYouPlotsCacheKey(districtId), ForYouCacheTtl,
            () => unitOfWork.PlotListings.GetHomeForYouProjectedAsync(districtId, MaxLimit));
        return OkResponse(new { items = items.Take(ClampLimit(limit)) });
    }

    public static async Task<IResult> GetRoomsBrowse(
        Guid districtId, Guid? cityId, Guid? roomTypeId, string? sortBy, int page, int pageSize, string? cursor,
        IUnitOfWork unitOfWork, IMemoryCache cache, IServiceProvider sp)
    {
        if (await IsTabInactiveAsync(AppTabKeys.Rooms, unitOfWork, cache))
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
        if (await IsTabInactiveAsync(AppTabKeys.Plots, unitOfWork, cache))
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

    // Deliberately separate from GetRooms/GetRoomsBrowse rather than an optional-districtId overload of either:
    // no district/city locality to filter or rank by, and identical for every caller, so it is cached.
    public static async Task<IResult> GetRecentRooms(int limit, IUnitOfWork unitOfWork, IMemoryCache cache, IServiceProvider sp)
    {
        if (await IsTabInactiveAsync(AppTabKeys.Rooms, unitOfWork, cache)) return OkResponse(new { items = Array.Empty<HomeRoomDto>() });
        var items = await ReadThroughListAsync(sp.GetService<IConnectionMultiplexer>(), RecentRoomsCacheKey, RecentCacheTtl,
            () => unitOfWork.RoomListings.GetRecentProjectedAsync(MaxLimit));
        return OkResponse(new { items = items.Take(ClampLimit(limit)) });
    }

    public static async Task<IResult> GetRecentPlots(int limit, IUnitOfWork unitOfWork, IMemoryCache cache, IServiceProvider sp)
    {
        if (await IsTabInactiveAsync(AppTabKeys.Plots, unitOfWork, cache)) return OkResponse(new { items = Array.Empty<HomePlotDto>() });
        var items = await ReadThroughListAsync(sp.GetService<IConnectionMultiplexer>(), RecentPlotsCacheKey, RecentCacheTtl,
            () => unitOfWork.PlotListings.GetRecentProjectedAsync(MaxLimit));
        return OkResponse(new { items = items.Take(ClampLimit(limit)) });
    }

    // ---- Aggregate Home feed ----

    public static class FeedSections
    {
        public const string Rooms = "rooms";
        public const string Plots = "plots";
        public const string Stays = "stays";
        public const string Taxis = "taxis";
        public const string RecentRooms = "recentRooms";
        public const string RecentPlots = "recentPlots";
        public const string RecentStays = "recentStays";
    }

    private const int FeedDefaultLimit = 5;
    private const int FeedMaxLimit = 10;
    private const int FeedDefaultRecentLimit = 10;

    private sealed record FeedSlot(string Section, string Key, TimeSpan Ttl);

    // One call for every Home rail. Off tabs are skipped before any Redis/DB work and come back null; all cached
    // sections are read with ONE Redis MGET; only misses hit the DB, sequentially on the request's DbContext, and
    // each miss is written back to the key (and TTL) its own endpoint uses, so existing invalidation covers it.
    public static async Task<IResult> GetFeed(
        Guid? districtId, Guid? cityId, int? limit, int? recentLimit, string? sections,
        IUnitOfWork unitOfWork, IMemoryCache cache, IServiceProvider sp)
    {
        if (districtId is not Guid district || district == Guid.Empty)
            return BadRequestResponse("districtId is required");

        var take = Math.Clamp(limit ?? FeedDefaultLimit, 1, FeedMaxLimit);
        var recentTake = Math.Clamp(recentLimit ?? FeedDefaultRecentLimit, 1, MaxLimit);

        var tabs = await ConfigHandlers.GetAppTabsCachedAsync(unitOfWork, cache);
        var feed = new HomeFeedDto
        {
            Tabs = new HomeFeedTabsDto
            {
                Rooms = TabState.IsActive(tabs, AppTabKeys.Rooms),
                Plots = TabState.IsActive(tabs, AppTabKeys.Plots),
                Stays = TabState.IsActive(tabs, AppTabKeys.Stays),
                Jobs = TabState.IsActive(tabs, AppTabKeys.Jobs),
                Taxi = TabState.IsActive(tabs, AppTabKeys.Taxi),
            },
        };

        HashSet<string>? requested = string.IsNullOrWhiteSpace(sections)
            ? null
            : sections.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool Want(string section, bool tabOn) => tabOn && (requested == null || requested.Contains(section));

        var slots = new List<FeedSlot>(7);
        if (Want(FeedSections.Rooms, feed.Tabs.Rooms))
            slots.Add(new FeedSlot(FeedSections.Rooms, ForYouRoomsCacheKey(district), ForYouCacheTtl));
        if (Want(FeedSections.Plots, feed.Tabs.Plots))
            slots.Add(new FeedSlot(FeedSections.Plots, ForYouPlotsCacheKey(district), ForYouCacheTtl));
        if (Want(FeedSections.Stays, feed.Tabs.Stays))
            slots.Add(new FeedSlot(FeedSections.Stays, StayCache.BrowseKey(district, cityId, null, null, "newest", take), StayCache.BrowseTtl));
        if (Want(FeedSections.Taxis, feed.Tabs.Taxi))
            slots.Add(new FeedSlot(FeedSections.Taxis, TaxiCache.LocalKey(district, cityId, null, take), TaxiCache.LocalTtl));
        if (Want(FeedSections.RecentRooms, feed.Tabs.Rooms))
            slots.Add(new FeedSlot(FeedSections.RecentRooms, RecentRoomsCacheKey, RecentCacheTtl));
        if (Want(FeedSections.RecentPlots, feed.Tabs.Plots))
            slots.Add(new FeedSlot(FeedSections.RecentPlots, RecentPlotsCacheKey, RecentCacheTtl));
        if (Want(FeedSections.RecentStays, feed.Tabs.Stays))
            slots.Add(new FeedSlot(FeedSections.RecentStays, StayCache.RecentKey, StayCache.RecentTtl));

        if (slots.Count == 0) return OkResponse(feed);

        var redis = sp.GetService<IConnectionMultiplexer>();
        var cached = new RedisValue[slots.Count];
        if (redis != null)
        {
            try { cached = await redis.GetDatabase().StringGetAsync(slots.Select(s => (RedisKey)s.Key).ToArray()); }
            catch { cached = new RedisValue[slots.Count]; }
        }

        var writes = new List<(string Key, string Json, TimeSpan Ttl)>();

        async Task<T> ResolveAsync<T>(int index, Func<Task<T>> load) where T : class
        {
            var hit = Deserialize<T>(cached[index]);
            if (hit != null) return hit;
            var fresh = await load();
            if (redis != null) writes.Add((slots[index].Key, JsonSerializer.Serialize(fresh), slots[index].Ttl));
            return fresh;
        }

        for (var i = 0; i < slots.Count; i++)
        {
            switch (slots[i].Section)
            {
                case FeedSections.Rooms:
                    feed.Rooms = (await ResolveAsync(i, () => unitOfWork.RoomListings.GetHomeForYouProjectedAsync(district, MaxLimit)))
                        .Take(take).ToList();
                    break;
                case FeedSections.Plots:
                    feed.Plots = (await ResolveAsync(i, () => unitOfWork.PlotListings.GetHomeForYouProjectedAsync(district, MaxLimit)))
                        .Take(take).ToList();
                    break;
                case FeedSections.Stays:
                    feed.Stays = (await ResolveAsync(i, () => unitOfWork.Stays.BrowsePagedAsync(district, cityId, null, null, "newest", 1, take, null)))
                        .Items.ToList();
                    break;
                case FeedSections.Taxis:
                    feed.Taxis = (await ResolveAsync(i, async () =>
                    {
                        var (items, hasMore) = await unitOfWork.Taxis.BrowseLocalPagedAsync(district, cityId, null, 1, take);
                        return new TaxiHandlers.TaxiPage(items.ToList(), hasMore);
                    })).Items;
                    break;
                case FeedSections.RecentRooms:
                    feed.RecentRooms = (await ResolveAsync(i, () => unitOfWork.RoomListings.GetRecentProjectedAsync(MaxLimit)))
                        .Take(recentTake).ToList();
                    break;
                case FeedSections.RecentPlots:
                    feed.RecentPlots = (await ResolveAsync(i, () => unitOfWork.PlotListings.GetRecentProjectedAsync(MaxLimit)))
                        .Take(recentTake).ToList();
                    break;
                case FeedSections.RecentStays:
                    feed.RecentStays = (await ResolveAsync(i, () => unitOfWork.Stays.GetRecentProjectedAsync(StayCache.RecentMax)))
                        .Take(recentTake).ToList();
                    break;
            }
        }

        if (redis != null && writes.Count > 0)
        {
            try
            {
                var db = redis.GetDatabase();
                await Task.WhenAll(writes.Select(w => db.StringSetAsync(w.Key, w.Json, w.Ttl)));
            }
            catch { }
        }

        return OkResponse(feed);
    }
}
