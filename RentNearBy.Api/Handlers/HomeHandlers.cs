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
    private const int MaxLimit = 20;

    // Corrupted entry => miss (falls through to the DB).
    private static T? Deserialize<T>(RedisValue value) where T : class
    {
        if (!value.HasValue) return null;
        try { return JsonSerializer.Deserialize<T>(value!); }
        catch (JsonException) { return null; }
    }

    // ---- Aggregate Home feed ----

    public static class FeedSections
    {
        public const string Stays = "stays";
        public const string Taxis = "taxis";
        public const string RecentStays = "recentStays";
    }

    private const int FeedDefaultLimit = 5;
    private const int FeedMaxLimit = 10;
    private const int FeedDefaultRecentLimit = 10;

    // GenKey: the stay/taxi generation the entry is stamped with (CacheStamp); null = plain value (recent stays).
    private sealed record FeedSlot(string Section, string Key, TimeSpan Ttl, string? GenKey = null);

    // One call for every Home rail. Off tabs are skipped before any Redis/DB work and come back null; all cached
    // sections are read with ONE MGET; only misses hit the DB, sequentially on the request's DbContext, and each
    // miss is written back to the key (and TTL) its own endpoint uses.
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

        var slots = new List<FeedSlot>(3);
        if (Want(FeedSections.Stays, feed.Tabs.Stays))
            slots.Add(new FeedSlot(FeedSections.Stays, StayCache.BrowseKey(district, cityId, null, null, "newest", take), StayCache.BrowseTtl,
                StayCache.GenKey(district)));
        if (Want(FeedSections.Taxis, feed.Tabs.Taxi))
            slots.Add(new FeedSlot(FeedSections.Taxis, TaxiCache.LocalKey(district, cityId, null, take), TaxiCache.LocalTtl,
                TaxiCache.GenKey(district)));
        if (Want(FeedSections.RecentStays, feed.Tabs.Stays))
            slots.Add(new FeedSlot(FeedSections.RecentStays, StayCache.RecentKey, StayCache.RecentTtl));

        if (slots.Count == 0) return OkResponse(feed);

        // One MGET: every section key, then the generation keys of the stamped sections.
        var cached = new RedisValue[slots.Count];
        var generations = new long?[slots.Count];
        if (redis != null)
        {
            var genKeys = slots.Where(s => s.GenKey != null).Select(s => s.GenKey!).Distinct().ToList();
            try
            {
                var values = await redis.GetDatabase().StringGetAsync(
                    slots.Select(s => (RedisKey)s.Key).Concat(genKeys.Select(k => (RedisKey)k)).ToArray());
                for (var i = 0; i < slots.Count; i++)
                {
                    if (slots[i].GenKey is not { } genKey)
                    {
                        cached[i] = values[i];
                        continue;
                    }
                    var generation = CacheStamp.Generation(values[slots.Count + genKeys.IndexOf(genKey)]);
                    generations[i] = generation;
                    cached[i] = CacheStamp.Unwrap(values[i], generation);
                }
            }
            catch
            {
                cached = new RedisValue[slots.Count];
                generations = new long?[slots.Count];
            }
        }

        var writes = new List<(string Key, string Json, TimeSpan Ttl)>();

        async Task<T> ResolveAsync<T>(int index, Func<Task<T>> load) where T : class
        {
            var hit = Deserialize<T>(cached[index]);
            if (hit != null) return hit;
            var fresh = await load();
            var json = JsonSerializer.Serialize(fresh);
            if (redis != null && slots[index].GenKey == null) writes.Add((slots[index].Key, json, slots[index].Ttl));
            else if (redis != null && generations[index] is { } g) writes.Add((slots[index].Key, CacheStamp.Wrap(g, json), slots[index].Ttl));
            return fresh;
        }

        for (var i = 0; i < slots.Count; i++)
        {
            switch (slots[i].Section)
            {
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
