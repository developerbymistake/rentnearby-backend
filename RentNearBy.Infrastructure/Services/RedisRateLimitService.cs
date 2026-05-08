using RentNearBy.Core.Interfaces;
using StackExchange.Redis;

namespace RentNearBy.Infrastructure.Services;

public class RedisRateLimitService : IRateLimitService
{
    private readonly IConnectionMultiplexer _redis;

    // Atomically increments the counter. Sets expiry only on first increment so
    // the window doesn't reset on every call. Returns [count, ttl_seconds].
    private static readonly string _script = """
        local current = redis.call('INCR', KEYS[1])
        if current == 1 then
            redis.call('EXPIRE', KEYS[1], ARGV[1])
        end
        local ttl = redis.call('TTL', KEYS[1])
        return {current, ttl}
        """;

    public RedisRateLimitService(IConnectionMultiplexer redis) => _redis = redis;

    public async Task<RateLimitResult> CheckAsync(string key, int maxAttempts, TimeSpan window)
    {
        try
        {
            var db = _redis.GetDatabase();
            var result = (RedisResult[]?)await db.ScriptEvaluateAsync(
                _script,
                keys: [new RedisKey(key)],
                values: [(RedisValue)(long)window.TotalSeconds]);

            if (result is null) return Allow(maxAttempts);

            var count = (long)result[0];
            var ttl = (long)result[1];
            var retryAfter = ttl > 0 ? TimeSpan.FromSeconds(ttl) : window;

            if (count > maxAttempts)
                return new RateLimitResult(false, 0, retryAfter);

            return new RateLimitResult(true, (int)(maxAttempts - count), null);
        }
        catch
        {
            // Fail open — Redis down should never block login
            return Allow(maxAttempts);
        }
    }

    private static RateLimitResult Allow(int maxAttempts) =>
        new(true, maxAttempts, null);
}
