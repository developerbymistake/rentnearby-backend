using RentNearBy.Core.Models;
using StackExchange.Redis;

namespace RentNearBy.Infrastructure.Services;

// Stay and taxi feed entries stamped with their district (or search) generation (CacheStamp). Best effort: a Redis
// failure reads as a miss with no generation, and the caller then skips the write.
public static class FeedCache
{
    public static async Task<(string? Payload, long? Generation)> ReadAsync(IConnectionMultiplexer? redis, string key, string genKey)
    {
        if (redis == null) return (null, null);
        try
        {
            var values = await redis.GetDatabase().StringGetAsync(new RedisKey[] { key, genKey });
            var generation = CacheStamp.Generation(values[1]);
            return (CacheStamp.Unwrap(values[0], generation), generation);
        }
        catch
        {
            return (null, null);
        }
    }

    // Several generations stamp one entry: the stamp is their sum (each only grows, so any bump changes it). One MGET.
    public static async Task<(string? Payload, long? Generation)> ReadManyAsync(IConnectionMultiplexer? redis, string key, IReadOnlyList<string> genKeys)
    {
        if (redis == null) return (null, null);
        try
        {
            var keys = new RedisKey[genKeys.Count + 1];
            keys[0] = key;
            for (var i = 0; i < genKeys.Count; i++) keys[i + 1] = genKeys[i];
            var values = await redis.GetDatabase().StringGetAsync(keys);
            var generation = SumStamp(values.Skip(1).Select(v => (string?)v));
            return (CacheStamp.Unwrap(values[0], generation), generation);
        }
        catch
        {
            return (null, null);
        }
    }

    public static long SumStamp(IEnumerable<string?> generations) => generations.Sum(CacheStamp.Generation);

    public static async Task WriteAsync(IConnectionMultiplexer? redis, string key, long? generation, string payload, TimeSpan ttl)
    {
        if (redis == null || generation is not { } g) return;
        try { await redis.GetDatabase().StringSetAsync(key, CacheStamp.Wrap(g, payload), ttl); } catch { }
    }

    public static async Task BumpAsync(IConnectionMultiplexer? redis, IReadOnlyCollection<string> genKeys)
    {
        if (redis == null || genKeys.Count == 0) return;
        try
        {
            var db = redis.GetDatabase();
            await Task.WhenAll(genKeys.Distinct().Select(k => db.StringIncrementAsync(k)));
        }
        catch { /* best-effort: TTL (<= 60s) covers Redis failures */ }
    }
}
