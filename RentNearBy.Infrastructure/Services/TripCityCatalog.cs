using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Core.Utils;
using StackExchange.Redis;

namespace RentNearBy.Infrastructure.Services;

// Every Redis call is guarded; IAtlasClient is resolved per fetch like TripSpotCache.
public class TripCityCatalog(
    IOptions<TripPlannerOptions> options,
    IServiceProvider sp,
    ILogger<TripCityCatalog> logger) : ITripCityCatalog
{
    private const int LockWaitRounds = 10;
    private static readonly TimeSpan LockWaitDelay = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan WarnEvery = TimeSpan.FromMinutes(1);

    private sealed class Entry(TripCityCatalogSnapshot snapshot)
    {
        public TripCityCatalogSnapshot Snapshot { get; } = snapshot;
        public Dictionary<long, TripCityInfo> Index { get; } = snapshot.Cities
            .GroupBy(c => c.CityKey)
            .ToDictionary(g => g.Key, g => g.First());
    }

    private readonly object _gate = new();
    private Entry? _copy;
    private int _generation;
    private Lazy<Task<Entry?>>? _inflight;
    private Lazy<Task<Entry?>>? _revalidating;
    private DateTime _lastWarnAt = DateTime.MinValue;

    public async Task<TripCityCatalogSnapshot?> GetAsync(CancellationToken ct = default)
        => (await GetEntryAsync(ct))?.Snapshot;

    public async Task<(bool Available, TripCityInfo? City)> FindAsync(long cityKey, CancellationToken ct = default)
    {
        var entry = await GetEntryAsync(ct);
        if (entry == null) return (false, null);
        return (true, entry.Index.GetValueOrDefault(cityKey));
    }

    public async Task InvalidateAsync()
    {
        lock (_gate)
        {
            _generation++;
            if (_copy != null) _copy.Snapshot.FetchedAt = DateTime.MinValue;
        }

        var redis = sp.GetService<IConnectionMultiplexer>();
        if (redis == null) return;
        try
        {
            await redis.GetDatabase().KeyDeleteAsync(TripCacheKeys.CityCatalog);
        }
        catch (Exception ex)
        {
            logger.LogWarning("TripCityCatalog: invalidate failed: {Error}", ex.Message);
        }
    }

    public static IReadOnlyList<TripCityInfo> FilterCities(IReadOnlyList<TripCityInfo> cities, string? q)
        => FilterCities(cities, q, popularOnly: false, stateName: null);

    // Folded q (accents and case ignored). Blank q = popular first, then the rest, in catalog order;
    // else Name prefix matches first, then Name or DistrictName contains, each in catalog order.
    public static IReadOnlyList<TripCityInfo> FilterCities(
        IReadOnlyList<TripCityInfo> cities, string? q, bool popularOnly, string? stateName)
    {
        var state = stateName?.Trim();
        IEnumerable<TripCityInfo> scope = cities;
        if (popularOnly) scope = scope.Where(c => c.IsPopularDestination);
        if (!string.IsNullOrEmpty(state))
            scope = scope.Where(c => string.Equals(c.StateName, state, StringComparison.OrdinalIgnoreCase));

        var folded = SearchKeys.Fold(q);
        if (folded.Length == 0)
        {
            var all = scope.ToList();
            return all.Where(c => c.IsPopularDestination).Concat(all.Where(c => !c.IsPopularDestination)).ToList();
        }

        var prefix = new List<TripCityInfo>();
        var contains = new List<TripCityInfo>();
        foreach (var c in scope)
        {
            var name = SearchKeys.Fold(c.Name);
            if (name.StartsWith(folded, StringComparison.Ordinal)) prefix.Add(c);
            else if (name.Contains(folded, StringComparison.Ordinal)
                     || SearchKeys.Fold(c.DistrictName).Contains(folded, StringComparison.Ordinal)) contains.Add(c);
        }
        prefix.AddRange(contains);
        return prefix;
    }

    private async Task<Entry?> GetEntryAsync(CancellationToken ct)
    {
        var redis = sp.GetService<IConnectionMultiplexer>();

        Entry? copy;
        lock (_gate) copy = _copy;

        if (copy == null)
        {
            var stored = await ReadAsync(redis, TripCacheKeys.CityCatalog);
            if (stored != null)
            {
                stored.IsStale = false;
                lock (_gate)
                {
                    _copy ??= new Entry(stored);
                    copy = _copy;
                }
            }
        }

        if (copy == null) return await ReloadSharedAsync(redis, ct);

        bool fresh;
        lock (_gate)
            fresh = DateTime.UtcNow - copy.Snapshot.FetchedAt < TimeSpan.FromSeconds(options.Value.CityCatalogRevalidateSeconds);
        if (fresh) return copy;

        return await RevalidateSharedAsync(redis, copy, ct);
    }

    // Single flights: the field is cleared when the shared work ends, whatever the callers do.
    private Task<Entry?> ReloadSharedAsync(IConnectionMultiplexer? redis, CancellationToken ct)
    {
        Lazy<Task<Entry?>> lazy;
        lock (_gate)
        {
            _inflight ??= new Lazy<Task<Entry?>>(() => ReloadOnceAsync(redis));
            lazy = _inflight;
        }
        return lazy.Value.WaitAsync(ct);
    }

    private async Task<Entry?> ReloadOnceAsync(IConnectionMultiplexer? redis)
    {
        try
        {
            return await ReloadAsync(redis);
        }
        finally
        {
            lock (_gate) _inflight = null;
        }
    }

    private Task<Entry?> RevalidateSharedAsync(IConnectionMultiplexer? redis, Entry copy, CancellationToken ct)
    {
        Lazy<Task<Entry?>> lazy;
        lock (_gate)
        {
            _revalidating ??= new Lazy<Task<Entry?>>(() => RevalidateOnceAsync(redis, copy));
            lazy = _revalidating;
        }
        return lazy.Value.WaitAsync(ct);
    }

    private async Task<Entry?> RevalidateOnceAsync(IConnectionMultiplexer? redis, Entry copy)
    {
        try
        {
            return await RevalidateAsync(redis, copy);
        }
        finally
        {
            lock (_gate) _revalidating = null;
        }
    }

    private async Task<Entry?> RevalidateAsync(IConnectionMultiplexer? redis, Entry copy)
    {
        int gen;
        lock (_gate) gen = _generation;

        AtlasCatalogVersion version;
        try
        {
            var atlas = sp.GetRequiredService<IAtlasClient>();
            if (!atlas.IsConfigured) throw new AtlasUnavailableException("Atlas is not configured");
            version = await atlas.GetCatalogVersionAsync();
        }
        catch (Exception ex)
        {
            lock (_gate) copy.Snapshot.FetchedAt = DateTime.UtcNow;
            WarnThrottled("TripCityCatalog: version check failed: {Error}", ex.Message);
            return copy;
        }

        if (TripCityCatalogSnapshot.VersionOf(version.UpdatedAt, version.Count) != copy.Snapshot.Version)
            return await ReloadSharedAsync(redis, CancellationToken.None);

        lock (_gate)
        {
            if (_generation != gen) return copy;
            copy.Snapshot.FetchedAt = DateTime.UtcNow;
        }
        await StoreFreshAsync(redis, copy.Snapshot, gen, writeStale: false);
        return copy;
    }

    private async Task<Entry?> ReloadAsync(IConnectionMultiplexer? redis)
    {
        var o = options.Value;
        int gen;
        lock (_gate) gen = _generation;

        var acquired = await TryAcquireLockAsync(redis, TimeSpan.FromSeconds(o.LockSeconds));
        if (acquired == false)
        {
            for (var i = 0; i < LockWaitRounds; i++)
            {
                await Task.Delay(LockWaitDelay);
                var stored = await ReadAsync(redis, TripCacheKeys.CityCatalog);
                if (stored == null) continue;
                stored.IsStale = false;
                var entry = new Entry(stored);
                lock (_gate)
                    if (_generation == gen) _copy = entry;
                return entry;
            }
        }

        try
        {
            TripCityCatalogSnapshot snapshot;
            try
            {
                snapshot = await FetchAsync(o);
            }
            catch (Exception ex)
            {
                logger.LogWarning("TripCityCatalog: Atlas cities unavailable: {Error}", ex.Message);
                return await FallbackAsync(redis);
            }

            var entry = new Entry(snapshot);
            lock (_gate)
            {
                if (_generation != gen) return entry;
                _copy = entry;
            }
            await StoreFreshAsync(redis, snapshot, gen, writeStale: true);
            StartCoverWarm(redis, entry, gen);
            return entry;
        }
        finally
        {
            if (acquired == true) await DeleteAsync(redis, TripCacheKeys.CityCatalogLock);
        }
    }

    // Writes the Redis copies; an invalidate that lands during the write removes the fresh key again.
    private async Task StoreFreshAsync(IConnectionMultiplexer? redis, TripCityCatalogSnapshot snapshot, int gen, bool writeStale)
    {
        var o = options.Value;
        var json = JsonSerializer.Serialize(snapshot, TripJson.Options);
        await WriteAsync(redis, TripCacheKeys.CityCatalog, json, TimeSpan.FromHours(o.CityCatalogTtlHours));
        if (writeStale)
            await WriteAsync(redis, TripCacheKeys.CityCatalogStale, json, TimeSpan.FromDays(o.CityCatalogStaleTtlDays));

        bool moved;
        lock (_gate) moved = _generation != gen;
        if (moved) await DeleteAsync(redis, TripCacheKeys.CityCatalog);
    }

    private async Task<TripCityCatalogSnapshot> FetchAsync(TripPlannerOptions o)
    {
        var atlas = sp.GetRequiredService<IAtlasClient>();
        if (!atlas.IsConfigured) throw new AtlasUnavailableException("Atlas is not configured");

        for (var attempt = 0; ; attempt++)
        {
            var items = new List<AtlasCity>();
            AtlasCityPage? first = null;
            var restart = false;

            for (var page = 1; ; page++)
            {
                var p = await atlas.SearchCitiesAsync(null, page, o.CityCatalogPageSize, false);
                if (first == null) first = p;
                else if (attempt == 0 && (p.CatalogUpdatedAt != first.CatalogUpdatedAt || p.CatalogCount != first.CatalogCount))
                {
                    restart = true;
                    break;
                }

                items.AddRange(p.Items ?? new List<AtlasCity>());
                if (!p.HasMore) break;
                if (page >= o.CityCatalogMaxPages)
                {
                    logger.LogWarning("TripCityCatalog: page cap {MaxPages} reached, keeping {Count} cities", o.CityCatalogMaxPages, items.Count);
                    break;
                }
            }

            if (!restart)
            {
                var snapshot = Build(first!, items);
                var uncached = await ApplyCoversAsync(snapshot.Cities);
                _pendingCovers = uncached;
                return snapshot;
            }
        }
    }

    internal static TripCityCatalogSnapshot Build(AtlasCityPage first, IEnumerable<AtlasCity> items) => new()
    {
        Format = TripCityCatalogSnapshot.CurrentFormat,
        Version = TripCityCatalogSnapshot.VersionOf(first.CatalogUpdatedAt, first.CatalogCount),
        UpdatedAt = first.CatalogUpdatedAt,
        Count = first.CatalogCount,
        FetchedAt = DateTime.UtcNow,
        IsStale = false,
        Cities = items
            .DistinctBy(c => c.CityKey)
            .Select(c => new TripCityInfo
            {
                CityKey = c.CityKey,
                Name = c.Name,
                DistrictName = c.DistrictName,
                StateName = c.StateName,
                Latitude = c.Latitude,
                Longitude = c.Longitude,
                ImageUrl = c.ImageUrl,
                IsPopularDestination = c.IsPopularDestination,
                Status = c.Status,
                SpotCount = c.SpotCount,
            })
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.CityKey)
            .ToList(),
    };

    internal const int CoverParallelism = 8;
    internal const int WarmParallelism = 4;

    private List<TripCityInfo> _pendingCovers = new();

    // Cities that still need a cover and whose spot set is not cached yet.
    private static bool NeedsCover(TripCityInfo c)
        => string.IsNullOrWhiteSpace(c.ImageUrl) && c.SpotCount > 0
           && string.Equals(c.Status, TripSpotStatuses.Ready, StringComparison.OrdinalIgnoreCase);

    // The city image wins. With the real spot cache the cover comes from cached spot sets only (no Atlas call), so a cold
    // list never waits on one fetch per city; the cities left over are warmed in the background. Any other cache keeps the
    // read-through behaviour. Returns the cities still waiting for their spot set.
    private async Task<List<TripCityInfo>> ApplyCoversAsync(List<TripCityInfo> cities)
    {
        foreach (var c in cities) c.CoverUrl = CloudinaryThumb.Apply(c.ImageUrl);

        var spots = sp.GetService<ITripSpotCache>();
        if (spots == null) return new List<TripCityInfo>();

        var missing = cities.Where(NeedsCover).ToList();
        if (missing.Count == 0) return new List<TripCityInfo>();

        var peek = spots as ITripSpotPeek;
        var uncached = new System.Collections.Concurrent.ConcurrentBag<TripCityInfo>();
        await Parallel.ForEachAsync(missing, new ParallelOptions { MaxDegreeOfParallelism = CoverParallelism }, async (city, ct) =>
        {
            try
            {
                var cached = peek != null ? await peek.PeekAsync(city.CityKey, ct) : await spots.GetAsync(city.CityKey, ct);
                if (cached == null && peek != null) uncached.Add(city);
                else city.CoverUrl = CloudinaryThumb.Apply(BestSpotImage(cached?.Spots));
            }
            catch (Exception ex)
            {
                logger.LogWarning("TripCityCatalog: cover of {CityKey} failed: {Error}", city.CityKey, ex.Message);
            }
        });
        return uncached.OrderBy(c => c.CityKey).ToList();
    }

    // Background: loads the missing spot sets with low parallelism, sets the covers in place and republishes the copy.
    // The list endpoint's ETag carries the cover count, so clients see the change on their next revalidation.
    private void StartCoverWarm(IConnectionMultiplexer? redis, Entry entry, int gen)
    {
        List<TripCityInfo> work;
        lock (_gate)
        {
            work = _pendingCovers;
            _pendingCovers = new List<TripCityInfo>();
        }
        if (work.Count == 0) return;
        _ = Task.Run(() => WarmCoversAsync(redis, entry, work, gen));
    }

    private async Task WarmCoversAsync(IConnectionMultiplexer? redis, Entry entry, List<TripCityInfo> work, int gen)
    {
        try
        {
            var spots = sp.GetService<ITripSpotCache>();
            if (spots == null) return;
            var changed = 0;
            await Parallel.ForEachAsync(work, new ParallelOptions { MaxDegreeOfParallelism = WarmParallelism }, async (city, ct) =>
            {
                try
                {
                    var cached = await spots.GetAsync(city.CityKey, ct);
                    var cover = CloudinaryThumb.Apply(BestSpotImage(cached?.Spots));
                    if (cover == null) return;
                    city.CoverUrl = cover;
                    Interlocked.Increment(ref changed);
                }
                catch (Exception ex)
                {
                    logger.LogWarning("TripCityCatalog: warm cover of {CityKey} failed: {Error}", city.CityKey, ex.Message);
                }
            });
            if (changed == 0) return;
            bool current;
            lock (_gate) current = _generation == gen && ReferenceEquals(_copy, entry);
            if (current) await StoreFreshAsync(redis, entry.Snapshot, gen, writeStale: false);
        }
        catch (Exception ex)
        {
            logger.LogWarning("TripCityCatalog: cover warm failed: {Error}", ex.Message);
        }
    }

    // Featured spot with an image first, else the first spot with an image in cached order.
    internal static string? BestSpotImage(IReadOnlyList<TripSpotDto>? spots)
    {
        if (spots == null) return null;
        var withImage = spots.Where(s => !string.IsNullOrWhiteSpace(s.ImageUrl)).ToList();
        return (withImage.FirstOrDefault(s => s.IsFeatured) ?? withImage.FirstOrDefault())?.ImageUrl;
    }

    // The in-process copy wins; the Redis stale copy is served but never adopted, so the next call retries Atlas.
    private async Task<Entry?> FallbackAsync(IConnectionMultiplexer? redis)
    {
        Entry? copy;
        lock (_gate) copy = _copy;
        if (copy != null) return new Entry(StaleClone(copy.Snapshot));

        var stale = await ReadAsync(redis, TripCacheKeys.CityCatalogStale);
        if (stale == null) return null;
        stale.IsStale = true;
        return new Entry(stale);
    }

    private static TripCityCatalogSnapshot StaleClone(TripCityCatalogSnapshot s) => new()
    {
        Format = s.Format,
        Version = s.Version,
        UpdatedAt = s.UpdatedAt,
        Count = s.Count,
        FetchedAt = s.FetchedAt,
        IsStale = true,
        Cities = s.Cities,
    };

    private void WarnThrottled(string message, string error)
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            if (now - _lastWarnAt < WarnEvery) return;
            _lastWarnAt = now;
        }
        logger.LogWarning(message, error);
    }

    private async Task<TripCityCatalogSnapshot?> ReadAsync(IConnectionMultiplexer? redis, string key)
    {
        if (redis == null) return null;
        try
        {
            var value = await redis.GetDatabase().StringGetAsync(key);
            if (value.IsNullOrEmpty) return null;
            var snapshot = JsonSerializer.Deserialize<TripCityCatalogSnapshot>(value.ToString(), TripJson.Options);
            // A copy from before the popular flag has no format key (0): treat as a miss.
            return snapshot is { Format: TripCityCatalogSnapshot.CurrentFormat } ? snapshot : null;
        }
        catch (Exception ex)
        {
            logger.LogWarning("TripCityCatalog: read of {Key} failed: {Error}", key, ex.Message);
            return null;
        }
    }

    private async Task WriteAsync(IConnectionMultiplexer? redis, string key, string json, TimeSpan ttl)
    {
        if (redis == null) return;
        try
        {
            await redis.GetDatabase().StringSetAsync(key, json, ttl);
        }
        catch (Exception ex)
        {
            logger.LogWarning("TripCityCatalog: write of {Key} failed: {Error}", key, ex.Message);
        }
    }

    // null = no Redis or Redis failed (no cross-process lock, fetch directly).
    private async Task<bool?> TryAcquireLockAsync(IConnectionMultiplexer? redis, TimeSpan ttl)
    {
        if (redis == null) return null;
        try
        {
            return await redis.GetDatabase().StringSetAsync(TripCacheKeys.CityCatalogLock, "1", ttl, When.NotExists);
        }
        catch (Exception ex)
        {
            logger.LogWarning("TripCityCatalog: lock failed: {Error}", ex.Message);
            return null;
        }
    }

    private async Task DeleteAsync(IConnectionMultiplexer? redis, string key)
    {
        if (redis == null) return;
        try
        {
            await redis.GetDatabase().KeyDeleteAsync(key);
        }
        catch (Exception ex)
        {
            logger.LogWarning("TripCityCatalog: delete of {Key} failed: {Error}", key, ex.Message);
        }
    }
}
