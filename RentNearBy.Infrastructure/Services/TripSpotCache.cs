using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using StackExchange.Redis;

namespace RentNearBy.Infrastructure.Services;

// Cache-only read: never calls Atlas and never starts a load. Null = nothing cached (or no Redis).
public interface ITripSpotPeek
{
    Task<CachedCitySpots?> PeekAsync(long cityKey, CancellationToken ct = default);
}

// Every Redis call is guarded: a Redis failure degrades to a direct Atlas call.
// IAtlasClient is resolved per fetch so the typed client's handler keeps rotating.
public class TripSpotCache(
    IOptions<TripPlannerOptions> options,
    IServiceProvider sp,
    ILogger<TripSpotCache> logger) : ITripSpotCache, ITripSpotPeek
{
    private const int LockWaitRounds = 10;
    private static readonly TimeSpan LockWaitDelay = TimeSpan.FromMilliseconds(300);

    private readonly ConcurrentDictionary<long, Lazy<Task<CachedCitySpots?>>> _inflight = new();

    public async Task<CachedCitySpots?> GetAsync(long cityKey, CancellationToken ct = default)
    {
        var redis = sp.GetService<IConnectionMultiplexer>();

        var hit = await ReadAsync(redis, TripCacheKeys.Spots(cityKey));
        if (hit != null) return hit;

        var lazy = _inflight.GetOrAdd(cityKey, key => new Lazy<Task<CachedCitySpots?>>(() => LoadOnceAsync(redis, key)));
        return await lazy.Value.WaitAsync(ct);
    }

    // Fresh copy first, then the 7-day stale copy; used by the city catalog so a cold city list never waits on Atlas.
    public async Task<CachedCitySpots?> PeekAsync(long cityKey, CancellationToken ct = default)
    {
        var redis = sp.GetService<IConnectionMultiplexer>();
        return await ReadAsync(redis, TripCacheKeys.Spots(cityKey)) ?? await ReadAsync(redis, TripCacheKeys.Stale(cityKey));
    }

    // Keeps the v3 stale copy so a failed reload answers it instead of 503; the legacy pair never comes back.
    internal static RedisKey[] InvalidateKeys(long cityKey) =>
        [TripCacheKeys.Spots(cityKey), TripCacheKeys.Legacy(cityKey), TripCacheKeys.LegacyStale(cityKey)];

    public async Task InvalidateAsync(long cityKey)
    {
        var redis = sp.GetService<IConnectionMultiplexer>();
        if (redis == null) return;
        try
        {
            await redis.GetDatabase().KeyDeleteAsync(InvalidateKeys(cityKey));
        }
        catch (Exception ex)
        {
            logger.LogWarning("TripSpotCache: invalidate of {CityKey} failed: {Error}", cityKey, ex.Message);
        }
    }

    // In-process single flight: the entry leaves the map when the shared load ends, whatever the callers do.
    private async Task<CachedCitySpots?> LoadOnceAsync(IConnectionMultiplexer? redis, long cityKey)
    {
        try
        {
            return await LoadAsync(redis, cityKey);
        }
        finally
        {
            _inflight.TryRemove(cityKey, out _);
        }
    }

    private async Task<CachedCitySpots?> LoadAsync(IConnectionMultiplexer? redis, long cityKey)
    {
        var o = options.Value;
        var lockKey = TripCacheKeys.Lock(cityKey);
        var acquired = await TryAcquireLockAsync(redis, lockKey, TimeSpan.FromSeconds(o.LockSeconds));

        if (acquired == false)
        {
            for (var i = 0; i < LockWaitRounds; i++)
            {
                await Task.Delay(LockWaitDelay);
                var fresh = await ReadAsync(redis, TripCacheKeys.Spots(cityKey));
                if (fresh != null) return fresh;
            }
        }

        try
        {
            return await FetchAsync(redis, cityKey, o);
        }
        finally
        {
            if (acquired == true) await DeleteAsync(redis, lockKey);
        }
    }

    private async Task<CachedCitySpots?> FetchAsync(IConnectionMultiplexer? redis, long cityKey, TripPlannerOptions o)
    {
        AtlasCitySpots? dto;
        try
        {
            var atlas = sp.GetRequiredService<IAtlasClient>();
            if (!atlas.IsConfigured) throw new AtlasUnavailableException("Atlas is not configured");
            dto = await atlas.GetCitySpotsAsync(cityKey);
        }
        catch (Exception ex)
        {
            logger.LogWarning("TripSpotCache: Atlas spots for {CityKey} unavailable: {Error}", cityKey, ex.Message);
            var stale = await ReadAsync(redis, TripCacheKeys.Stale(cityKey));
            if (stale == null)
            {
                var legacy = await ReadAsync(redis, TripCacheKeys.LegacyStale(cityKey));
                return legacy == null ? null : FromLegacy(legacy);
            }
            stale.Status = TripSpotStatuses.Stale;
            return stale;
        }

        var entry = ToCached(cityKey, dto, DateTime.UtcNow);
        var ready = entry.Status == TripSpotStatuses.Ready;
        var json = JsonSerializer.Serialize(entry, TripJson.Options);

        await WriteAsync(redis, TripCacheKeys.Spots(cityKey), json,
            ready ? TimeSpan.FromHours(o.CacheTtlHours) : TimeSpan.FromMinutes(o.NotReadyTtlMinutes));
        if (ready)
            await WriteAsync(redis, TripCacheKeys.Stale(cityKey), json, TimeSpan.FromDays(o.StaleTtlDays));

        return entry;
    }

    // A 404 (null) is treated as NotReady.
    internal static CachedCitySpots ToCached(long cityKey, AtlasCitySpots? dto, DateTime fetchedAt)
    {
        var ready = dto != null && dto.Status == TripSpotStatuses.Ready;
        var (spots, galleries) = ready ? Normalise(dto!.Spots) : (new List<TripSpotDto>(), new Dictionary<string, List<string>>());
        return new CachedCitySpots
        {
            CityKey = cityKey,
            CityName = dto?.CityName ?? string.Empty,
            Status = ready ? TripSpotStatuses.Ready : TripSpotStatuses.NotReady,
            GeneratedAt = ready ? dto!.GeneratedAt : null,
            FetchedAt = fetchedAt,
            Spots = spots,
            Galleries = galleries,
        };
    }

    // v2 stale copy (no galleries, up to 5 images): same normalisation, marked Stale, never written under v3.
    internal static CachedCitySpots FromLegacy(CachedCitySpots legacy)
    {
        var (spots, _) = Normalise(legacy.Spots);
        legacy.Spots = spots;
        legacy.Galleries = new();
        legacy.Status = TripSpotStatuses.Stale;
        return legacy;
    }

    // Images: distinct non-blank, first 3 (ImageUrl = first when blank). Gallery: distinct non-blank, first 10,
    // moved out of the spot. GalleryCount is recomputed, never trusted from Atlas. Blank ids are dropped.
    internal static (List<TripSpotDto> Spots, Dictionary<string, List<string>> Galleries) Normalise(List<TripSpotDto>? source)
    {
        var spots = new List<TripSpotDto>(source?.Count ?? 0);
        var galleries = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (source == null) return (spots, galleries);

        foreach (var spot in source)
        {
            if (spot == null || string.IsNullOrWhiteSpace(spot.Id)) continue;

            spot.Images = CleanUrls(spot.Images, TripSpotLimits.SpotPhotoMax);
            if (string.IsNullOrWhiteSpace(spot.ImageUrl)) spot.ImageUrl = spot.Images.Count > 0 ? spot.Images[0] : null;

            var gallery = CleanUrls(spot.Gallery, TripSpotLimits.SpotGalleryMax);
            spot.GalleryCount = gallery.Count;
            spot.Gallery = null;
            if (gallery.Count > 0) galleries[spot.Id] = gallery;

            spots.Add(spot);
        }
        return (spots, galleries);
    }

    private static List<string> CleanUrls(List<string>? urls, int max)
    {
        var result = new List<string>(Math.Min(urls?.Count ?? 0, max));
        if (urls == null) return result;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in urls)
        {
            if (result.Count == max) break;
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var url = raw.Trim();
            if (seen.Add(url)) result.Add(url);
        }
        return result;
    }

    private async Task<CachedCitySpots?> ReadAsync(IConnectionMultiplexer? redis, string key)
    {
        if (redis == null) return null;
        try
        {
            var value = await redis.GetDatabase().StringGetAsync(key);
            if (value.IsNullOrEmpty) return null;
            return JsonSerializer.Deserialize<CachedCitySpots>(value.ToString(), TripJson.Options);
        }
        catch (Exception ex)
        {
            logger.LogWarning("TripSpotCache: read of {Key} failed: {Error}", key, ex.Message);
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
            logger.LogWarning("TripSpotCache: write of {Key} failed: {Error}", key, ex.Message);
        }
    }

    // null = no Redis or Redis failed (no cross-process lock, fetch directly).
    private async Task<bool?> TryAcquireLockAsync(IConnectionMultiplexer? redis, string key, TimeSpan ttl)
    {
        if (redis == null) return null;
        try
        {
            return await redis.GetDatabase().StringSetAsync(key, "1", ttl, When.NotExists);
        }
        catch (Exception ex)
        {
            logger.LogWarning("TripSpotCache: lock {Key} failed: {Error}", key, ex.Message);
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
            logger.LogWarning("TripSpotCache: delete of {Key} failed: {Error}", key, ex.Message);
        }
    }
}
