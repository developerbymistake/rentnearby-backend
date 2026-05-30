using Microsoft.Extensions.Caching.Memory;
using RentNearBy.Core.Interfaces;

namespace RentNearBy.Infrastructure.Services;

public sealed class MemoryOtpStore : IOtpStore
{
    private readonly IMemoryCache _cache;

    public MemoryOtpStore(IMemoryCache cache) => _cache = cache;

    public Task SaveAsync(string phoneNumber, string otp, TimeSpan ttl)
    {
        _cache.Set(OtpKey(phoneNumber), otp, ttl);
        return Task.CompletedTask;
    }

    public Task<string?> GetAndDeleteAsync(string phoneNumber)
    {
        var key = OtpKey(phoneNumber);
        if (!_cache.TryGetValue(key, out string? otp))
            return Task.FromResult<string?>(null);

        _cache.Remove(key);
        return Task.FromResult(otp);
    }

    private static string OtpKey(string phone) => $"otp:{phone}";
}
