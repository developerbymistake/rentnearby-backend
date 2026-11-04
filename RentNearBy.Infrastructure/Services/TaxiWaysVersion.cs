using RentNearBy.Core.Models;
using StackExchange.Redis;

namespace RentNearBy.Infrastructure.Services;

// Network version: Redis taxi:ways:ver (shared by instances) plus a process counter, so a local change
// rebuilds this process even when the Redis INCR is lost.
public static class TaxiWaysVersion
{
    private static long _local;

    public static long Local => Interlocked.Read(ref _local);

    public static async Task BumpAsync(IConnectionMultiplexer? redis)
    {
        Interlocked.Increment(ref _local);
        if (redis == null) return;
        try { await redis.GetDatabase().StringIncrementAsync(TaxiWaysKeys.Ver); }
        catch { }
    }

    public static async Task<string> ReadAsync(IConnectionMultiplexer? redis) =>
        await TryReadAsync(redis) ?? $"L{Local}";

    // null when Redis is configured but failed: the caller keeps its current snapshot.
    public static async Task<string?> TryReadAsync(IConnectionMultiplexer? redis)
    {
        if (redis == null) return $"L{Local}";
        try
        {
            var value = await redis.GetDatabase().StringGetAsync(TaxiWaysKeys.Ver);
            return $"{(value.HasValue ? value.ToString() : "0")}-{Local}";
        }
        catch
        {
            return null;
        }
    }
}
