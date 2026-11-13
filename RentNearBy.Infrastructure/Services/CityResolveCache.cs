using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;
using StackExchange.Redis;

namespace RentNearBy.Infrastructure.Services;

// Reverse-geocoded area name per ~500 m grid cell. Memory plus Redis when present; a failed lookup is remembered for a minute only.
public sealed class CityResolveCache(IMemoryCache memory, IConnectionMultiplexer? redis = null)
{
    public const double CellDegrees = 0.0045;
    public static readonly TimeSpan MemoryTtl = TimeSpan.FromDays(1);
    public static readonly TimeSpan RedisTtl = TimeSpan.FromDays(7);
    public static readonly TimeSpan FailureTtl = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, Task<string?>> _inflight = new();

    public static (string Key, double Lat, double Lng) CellOf(double lat, double lng)
    {
        var latCell = (long)Math.Floor(lat / CellDegrees);
        var centreLat = (latCell + 0.5) * CellDegrees;
        var lngStep = CellDegrees / Math.Max(0.2, Math.Cos(centreLat * Math.PI / 180));
        var lngCell = (long)Math.Floor(lng / lngStep);
        return ($"{latCell}:{lngCell}", centreLat, (lngCell + 0.5) * lngStep);
    }

    // The name for the pin's cell ("" = answered, no name; null = lookup failed). fetch gets the cell centre.
    public async Task<string?> GetOrFetchAsync(double lat, double lng, Func<double, double, Task<string?>> fetch)
    {
        var (key, cLat, cLng) = CellOf(lat, lng);
        if (memory.TryGetValue(key, out string? hit)) return hit;
        if (memory.TryGetValue("f:" + key, out _)) return null;
        if (await RedisGetAsync(key) is { } stored)
        {
            memory.Set(key, stored, MemoryTtl);
            return stored;
        }

        var task = _inflight.GetOrAdd(key, _ => FetchAsync(key, cLat, cLng, fetch));
        try { return await task; }
        finally { _inflight.TryRemove(new KeyValuePair<string, Task<string?>>(key, task)); }
    }

    private async Task<string?> FetchAsync(string key, double lat, double lng, Func<double, double, Task<string?>> fetch)
    {
        var name = await fetch(lat, lng);
        if (name == null)
        {
            memory.Set("f:" + key, true, FailureTtl);
            return null;
        }
        memory.Set(key, name, MemoryTtl);
        await RedisSetAsync(key, name);
        return name;
    }

    private async Task<string?> RedisGetAsync(string key)
    {
        if (redis is not { IsConnected: true }) return null;
        try
        {
            var v = await redis.GetDatabase().StringGetAsync("cityarea:v1:" + key);
            return v.HasValue ? (string?)v : null;
        }
        catch { return null; }
    }

    private async Task RedisSetAsync(string key, string name)
    {
        if (redis is not { IsConnected: true }) return;
        try { await redis.GetDatabase().StringSetAsync("cityarea:v1:" + key, name, RedisTtl); }
        catch { }
    }
}
