using RentNearBy.Core.Interfaces;
using StackExchange.Redis;

namespace RentNearBy.Infrastructure.Services;

public sealed class RedisOtpStore : IOtpStore
{
    private readonly IConnectionMultiplexer _redis;

    // Atomically reads and deletes the OTP in a single round-trip — prevents
    // a race where two concurrent verify requests both read the same OTP.
    private static readonly string GetAndDeleteScript = """
        local val = redis.call('GET', KEYS[1])
        if val then redis.call('DEL', KEYS[1]) end
        return val
        """;

    public RedisOtpStore(IConnectionMultiplexer redis) => _redis = redis;

    public async Task SaveAsync(string phoneNumber, string otp, TimeSpan ttl)
    {
        var db = _redis.GetDatabase();
        await db.StringSetAsync(OtpKey(phoneNumber), otp, ttl);
    }

    public async Task<string?> GetAndDeleteAsync(string phoneNumber)
    {
        var db = _redis.GetDatabase();
        var result = await db.ScriptEvaluateAsync(
            GetAndDeleteScript,
            keys: [new RedisKey(OtpKey(phoneNumber))]);

        return result.IsNull ? null : (string?)result;
    }

    private static string OtpKey(string phone) => $"otp:{phone}";
}
