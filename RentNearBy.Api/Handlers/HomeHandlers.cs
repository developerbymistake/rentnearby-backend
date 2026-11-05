using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Services;
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

    // Room/plot keys are versioned by ListingCacheKeys generations (a bust is one INCR, see ListingCacheInvalidator).
    // The first browse page only (no cursor, page <= 1) is cached; pages after it never are.
    private static async Task<(IDatabase? Cache, string? Key)> VersionedKeyAsync(
        IConnectionMultiplexer? redis, string genKey, Func<long, string> keyOf)
    {
        if (redis == null) return (null, null);
        try
        {
            var db = redis.GetDatabase();
            return (db, keyOf(await ListingCacheGeneration.ReadAsync(db, genKey)));
        }
        catch { return (null, null); }
    }

    // Read-through: a hit skips the DB; a miss loads and writes back, living no longer than its first row's ValidUntil.
    private static async Task<T> ReadThroughAsync<T>(
        IDatabase? cache, string? key, TimeSpan ttl, Func<Task<T>> load, Func<T, IEnumerable<DateTime?>> validUntils) where T : class
    {
        if (cache != null && key != null)
        {
            RedisValue cached = default;
            try { cached = await cache.StringGetAsync(key); } catch { }
            var hit = Deserialize<T>(cached);
            if (hit != null) return hit;
        }

        var fresh = await load();
        if (cache != null && key != null && ListingVisibility.CacheTtl(ttl, DateTime.UtcNow, validUntils(fresh)) is TimeSpan life)
        {
            try { await cache.StringSetAsync(key, JsonSerializer.Serialize(fresh), life); } catch { }
        }
        return fresh;
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

    public static async Task<IResult> GetRooms(Guid districtId, int limit, IUnitOfWork unitOfWork, IMemoryCache cache, IServiceProvider sp, Guid? cityId = null)
    {
        if (await IsTabInactiveAsync(AppTabKeys.Rooms, unitOfWork, cache)) return OkResponse(new { items = Array.Empty<HomeRoomDto>() });
        var (redisDb, key) = await VersionedKeyAsync(sp.GetService<IConnectionMultiplexer>(),
            ListingCacheKeys.DistrictGen(ListingKinds.Room, districtId), gen => ListingCacheKeys.ForYou(ListingKinds.Room, districtId, gen, cityId));
        var items = await ReadThroughAsync(redisDb, key, ForYouCacheTtl,
            () => unitOfWork.RoomListings.GetHomeForYouProjectedAsync(districtId, cityId, MaxLimit), l => l.Select(x => x.ValidUntil));
        return OkResponse(new { items = items.Take(ClampLimit(limit)) });
    }

    public static async Task<IResult> GetPlots(Guid districtId, int limit, IUnitOfWork unitOfWork, IMemoryCache cache, IServiceProvider sp, Guid? cityId = null)
    {
        if (await IsTabInactiveAsync(AppTabKeys.Plots, unitOfWork, cache)) return OkResponse(new { items = Array.Empty<HomePlotDto>() });
        var (redisDb, key) = await VersionedKeyAsync(sp.GetService<IConnectionMultiplexer>(),
            ListingCacheKeys.DistrictGen(ListingKinds.Plot, districtId), gen => ListingCacheKeys.ForYou(ListingKinds.Plot, districtId, gen, cityId));
        var items = await ReadThroughAsync(redisDb, key, ForYouCacheTtl,
            () => unitOfWork.PlotListings.GetHomeForYouProjectedAsync(districtId, cityId, MaxLimit), l => l.Select(x => x.ValidUntil));
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
        var (redisDb, key) = await VersionedKeyAsync(firstPage ? sp.GetService<IConnectionMultiplexer>() : null,
            ListingCacheKeys.DistrictGen(ListingKinds.Room, districtId),
            gen => ListingCacheKeys.Browse(ListingKinds.Room, districtId, gen, cityId, roomTypeId, sort, size));

        var result = await ReadThroughAsync(redisDb, key, BrowseCacheTtl,
            () => unitOfWork.RoomListings.SearchPagedAsync(districtId, cityId, roomTypeId, sort, pageNo, size, cursor),
            p => p.Items.Select(x => x.ValidUntil));

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
        var (redisDb, key) = await VersionedKeyAsync(firstPage ? sp.GetService<IConnectionMultiplexer>() : null,
            ListingCacheKeys.DistrictGen(ListingKinds.Plot, districtId),
            gen => ListingCacheKeys.Browse(ListingKinds.Plot, districtId, gen, cityId, plotTypeId, sort, size));

        var result = await ReadThroughAsync(redisDb, key, BrowseCacheTtl,
            () => unitOfWork.PlotListings.GetAllPagedByTypeIdAsync(districtId, cityId, plotTypeId, sort, pageNo, size, cursor),
            p => p.Items.Select(x => x.ValidUntil));

        return OkResponse(new { items = result.Items, hasMore = result.HasMore, nextCursor = result.NextCursor });
    }

    // Deliberately separate from GetRooms/GetRoomsBrowse rather than an optional-districtId overload of either:
    // no district/city locality to filter or rank by, and identical for every caller, so it is cached.
    public static async Task<IResult> GetRecentRooms(int limit, IUnitOfWork unitOfWork, IMemoryCache cache, IServiceProvider sp)
    {
        if (await IsTabInactiveAsync(AppTabKeys.Rooms, unitOfWork, cache)) return OkResponse(new { items = Array.Empty<HomeRoomDto>() });
        var (redisDb, key) = await VersionedKeyAsync(sp.GetService<IConnectionMultiplexer>(),
            ListingCacheKeys.RecentGen(ListingKinds.Room), gen => ListingCacheKeys.Recent(ListingKinds.Room, gen));
        var items = await ReadThroughAsync(redisDb, key, RecentCacheTtl,
            () => unitOfWork.RoomListings.GetRecentProjectedAsync(MaxLimit), l => l.Select(x => x.ValidUntil));
        return OkResponse(new { items = items.Take(ClampLimit(limit)) });
    }

    public static async Task<IResult> GetRecentPlots(int limit, IUnitOfWork unitOfWork, IMemoryCache cache, IServiceProvider sp)
    {
        if (await IsTabInactiveAsync(AppTabKeys.Plots, unitOfWork, cache)) return OkResponse(new { items = Array.Empty<HomePlotDto>() });
        var (redisDb, key) = await VersionedKeyAsync(sp.GetService<IConnectionMultiplexer>(),
            ListingCacheKeys.RecentGen(ListingKinds.Plot), gen => ListingCacheKeys.Recent(ListingKinds.Plot, gen));
        var items = await ReadThroughAsync(redisDb, key, RecentCacheTtl,
            () => unitOfWork.PlotListings.GetRecentProjectedAsync(MaxLimit), l => l.Select(x => x.ValidUntil));
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

    // One call for every Home rail. Off tabs are skipped before any Redis/DB work and come back null; room/plot
    // generations are read with ONE MGET, then all cached sections with ONE MGET; only misses hit the DB,
    // sequentially on the request's DbContext, and each miss is written back to the key (and TTL) its own endpoint uses.
    // Rooms/plots "for you" are the first rows of the city-first browse walk, so cityId is part of their key.
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

        var redis = sp.GetService<IConnectionMultiplexer>();
        var genKeys = new[]
        {
            ListingCacheKeys.DistrictGen(ListingKinds.Room, district),
            ListingCacheKeys.DistrictGen(ListingKinds.Plot, district),
            ListingCacheKeys.RecentGen(ListingKinds.Room),
            ListingCacheKeys.RecentGen(ListingKinds.Plot),
        };
        var gens = new long[genKeys.Length];
        var anyVersioned = Want(FeedSections.Rooms, feed.Tabs.Rooms) || Want(FeedSections.Plots, feed.Tabs.Plots)
            || Want(FeedSections.RecentRooms, feed.Tabs.Rooms) || Want(FeedSections.RecentPlots, feed.Tabs.Plots);
        if (redis != null && anyVersioned)
        {
            try { gens = await ListingCacheGeneration.ReadManyAsync(redis.GetDatabase(), genKeys); }
            catch { gens = new long[genKeys.Length]; }
        }

        var slots = new List<FeedSlot>(7);
        if (Want(FeedSections.Rooms, feed.Tabs.Rooms))
            slots.Add(new FeedSlot(FeedSections.Rooms, ListingCacheKeys.ForYou(ListingKinds.Room, district, gens[0], cityId), ForYouCacheTtl));
        if (Want(FeedSections.Plots, feed.Tabs.Plots))
            slots.Add(new FeedSlot(FeedSections.Plots, ListingCacheKeys.ForYou(ListingKinds.Plot, district, gens[1], cityId), ForYouCacheTtl));
        if (Want(FeedSections.Stays, feed.Tabs.Stays))
            slots.Add(new FeedSlot(FeedSections.Stays, StayCache.BrowseKey(district, cityId, null, null, "newest", take), StayCache.BrowseTtl));
        if (Want(FeedSections.Taxis, feed.Tabs.Taxi))
            slots.Add(new FeedSlot(FeedSections.Taxis, TaxiCache.LocalKey(district, cityId, null, take), TaxiCache.LocalTtl));
        if (Want(FeedSections.RecentRooms, feed.Tabs.Rooms))
            slots.Add(new FeedSlot(FeedSections.RecentRooms, ListingCacheKeys.Recent(ListingKinds.Room, gens[2]), RecentCacheTtl));
        if (Want(FeedSections.RecentPlots, feed.Tabs.Plots))
            slots.Add(new FeedSlot(FeedSections.RecentPlots, ListingCacheKeys.Recent(ListingKinds.Plot, gens[3]), RecentCacheTtl));
        if (Want(FeedSections.RecentStays, feed.Tabs.Stays))
            slots.Add(new FeedSlot(FeedSections.RecentStays, StayCache.RecentKey, StayCache.RecentTtl));

        if (slots.Count == 0) return OkResponse(feed);

        var cached = new RedisValue[slots.Count];
        if (redis != null)
        {
            try { cached = await redis.GetDatabase().StringGetAsync(slots.Select(s => (RedisKey)s.Key).ToArray()); }
            catch { cached = new RedisValue[slots.Count]; }
        }

        var writes = new List<(string Key, string Json, TimeSpan Ttl)>();
        var now = DateTime.UtcNow;

        async Task<T> ResolveAsync<T>(int index, Func<Task<T>> load, Func<T, IEnumerable<DateTime?>>? validUntils = null) where T : class
        {
            var hit = Deserialize<T>(cached[index]);
            if (hit != null) return hit;
            var fresh = await load();
            var ttl = validUntils == null
                ? slots[index].Ttl
                : ListingVisibility.CacheTtl(slots[index].Ttl, now, validUntils(fresh));
            if (redis != null && ttl is TimeSpan life) writes.Add((slots[index].Key, JsonSerializer.Serialize(fresh), life));
            return fresh;
        }

        for (var i = 0; i < slots.Count; i++)
        {
            switch (slots[i].Section)
            {
                case FeedSections.Rooms:
                    feed.Rooms = (await ResolveAsync(i, () => unitOfWork.RoomListings.GetHomeForYouProjectedAsync(district, cityId, MaxLimit),
                            l => l.Select(x => x.ValidUntil)))
                        .Take(take).ToList();
                    break;
                case FeedSections.Plots:
                    feed.Plots = (await ResolveAsync(i, () => unitOfWork.PlotListings.GetHomeForYouProjectedAsync(district, cityId, MaxLimit),
                            l => l.Select(x => x.ValidUntil)))
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
                    feed.RecentRooms = (await ResolveAsync(i, () => unitOfWork.RoomListings.GetRecentProjectedAsync(MaxLimit),
                            l => l.Select(x => x.ValidUntil)))
                        .Take(recentTake).ToList();
                    break;
                case FeedSections.RecentPlots:
                    feed.RecentPlots = (await ResolveAsync(i, () => unitOfWork.PlotListings.GetRecentProjectedAsync(MaxLimit),
                            l => l.Select(x => x.ValidUntil)))
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
