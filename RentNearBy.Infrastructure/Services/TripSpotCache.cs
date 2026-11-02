using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using StackExchange.Redis;

namespace RentNearBy.Infrastructure.Services;

// Every Redis call is guarded: a Redis failure degrades to a direct Atlas call.
// IAtlasClient is resolved per fetch so the typed client's handler keeps rotating.
public class TripSpotCache(
    IOptions<TripPlannerOptions> options,
    IServiceProvider sp,
    ILogger<TripSpotCache> logger) : ITripSpotCache
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

    public async Task InvalidateAsync(long cityKey)
    {
        var redis = sp.GetService<IConnectionMultiplexer>();
        if (redis == null) return;
        try
        {
            await redis.GetDatabase().KeyDeleteAsync(new RedisKey[] { TripCacheKeys.Spots(cityKey), TripCacheKeys.Stale(cityKey) });
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
            if (stale == null) return null;
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
        return new CachedCitySpots
        {
            CityKey = cityKey,
            CityName = dto?.CityName ?? string.Empty,
            Status = ready ? TripSpotStatuses.Ready : TripSpotStatuses.NotReady,
            GeneratedAt = ready ? dto!.GeneratedAt : null,
            FetchedAt = fetchedAt,
            Spots = ready ? dto!.Spots ?? new() : new(),
        };
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
