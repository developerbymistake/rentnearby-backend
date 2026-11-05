using Microsoft.Extensions.Logging;
using RentNearBy.Core.Interfaces;
using StackExchange.Redis;

namespace RentNearBy.Infrastructure.Services;

public class RedisRateLimitService : IRateLimitService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisRateLimitService> _logger;

    // SlidingWindowLog in one atomic script: a sorted set of members scored by time (ms). Returns {allowed, remaining, retryMs}.
    // ARGV: now ms, window ms, max, permits, token.
    private static readonly string _acquire = """
        local key = KEYS[1]
        local now = tonumber(ARGV[1])
        local window = tonumber(ARGV[2])
        local max = tonumber(ARGV[3])
        local permits = tonumber(ARGV[4])
        local token = ARGV[5]
        redis.call('ZREMRANGEBYSCORE', key, '-inf', now - window)
        local used = redis.call('ZCARD', key)
        if used + permits <= max then
            for i = 0, permits - 1 do
                redis.call('ZADD', key, now, token .. ':' .. i)
            end
            redis.call('PEXPIRE', key, window)
            return {1, max - used - permits, 0}
        end
        local need = used + permits - max
        local retry = window
        if need <= used then
            local row = redis.call('ZRANGE', key, need - 1, need - 1, 'WITHSCORES')
            retry = tonumber(row[2]) + window - now
        end
        if retry < 1 then retry = 1 end
        local remaining = max - used
        if remaining < 0 then remaining = 0 end
        return {0, remaining, retry}
        """;

    // ARGV: token, permits.
    private static readonly string _release = """
        local n = 0
        for i = 0, tonumber(ARGV[2]) - 1 do
            n = n + redis.call('ZREM', KEYS[1], ARGV[1] .. ':' .. i)
        end
        return n
        """;

    // Sorted-set keys live apart from the old fixed-window string keys, so a leftover counter never hits WRONGTYPE.
    private static RedisKey KeyOf(string key) => new("sw:" + key);

    public RedisRateLimitService(IConnectionMultiplexer redis, ILogger<RedisRateLimitService> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    public async Task<RateLimitResult> CheckAsync(string key, int maxAttempts, TimeSpan window, int permits = 1)
    {
        permits = Math.Max(1, permits);
        var token = $"{Guid.NewGuid():N}.{permits}";
        try
        {
            var db = _redis.GetDatabase();
            var result = (RedisResult[]?)await db.ScriptEvaluateAsync(
                _acquire,
                keys: [KeyOf(key)],
                values:
                [
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    (long)window.TotalMilliseconds,
                    maxAttempts,
                    permits,
                    token,
                ]);

            if (result is null) return Allow(maxAttempts);

            var allowed = (long)result[0] == 1;
            var remaining = (int)(long)result[1];
            var retryMs = (long)result[2];
            return allowed
                ? new RateLimitResult(true, remaining, null, token)
                : new RateLimitResult(false, 0, TimeSpan.FromMilliseconds(retryMs));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Redis unavailable for key '{Key}' — failing open", key);
            return Allow(maxAttempts);
        }
    }

    public async Task ReleaseAsync(string key, string token)
    {
        var dot = token.LastIndexOf('.');
        if (dot < 0 || !int.TryParse(token.AsSpan(dot + 1), out var permits) || permits < 1) return;
        try
        {
            await _redis.GetDatabase().ScriptEvaluateAsync(
                _release, keys: [KeyOf(key)], values: [token, permits]);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Redis unavailable releasing key '{Key}'", key);
        }
    }

    private static RateLimitResult Allow(int maxAttempts) =>
        new(true, maxAttempts, null);
}
