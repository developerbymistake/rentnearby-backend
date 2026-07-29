using RentNearBy.Core.Interfaces;
using StackExchange.Redis;

namespace RentNearBy.Infrastructure.Services;

// Structural clone of RedisOtpStore — same atomic get-and-delete Lua script (prevents two concurrent
// requests for the same step from both consuming the same token), generalized to an arbitrary caller-
// supplied key instead of a phone-keyed one.
public sealed class RedisWebEnquiryTokenStore : IWebEnquiryTokenStore
{
    private readonly IConnectionMultiplexer _redis;

    private static readonly string GetAndDeleteScript = """
        local val = redis.call('GET', KEYS[1])
        if val then redis.call('DEL', KEYS[1]) end
        return val
        """;

    public RedisWebEnquiryTokenStore(IConnectionMultiplexer redis) => _redis = redis;

    public async Task SaveAsync(string key, string value, TimeSpan ttl)
    {
        var db = _redis.GetDatabase();
        await db.StringSetAsync(key, value, ttl);
    }

    public async Task<string?> GetAndDeleteAsync(string key)
    {
        var db = _redis.GetDatabase();
        var result = await db.ScriptEvaluateAsync(GetAndDeleteScript, keys: [new RedisKey(key)]);
        return result.IsNull ? null : (string?)result;
    }
}
