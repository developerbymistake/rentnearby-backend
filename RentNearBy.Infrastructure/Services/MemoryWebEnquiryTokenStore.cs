using Microsoft.Extensions.Caching.Memory;
using RentNearBy.Core.Interfaces;

namespace RentNearBy.Infrastructure.Services;

// Structural clone of MemoryOtpStore — local-dev/no-Redis fallback, same shape as the Redis-backed
// implementation above.
public sealed class MemoryWebEnquiryTokenStore : IWebEnquiryTokenStore
{
    private readonly IMemoryCache _cache;

    public MemoryWebEnquiryTokenStore(IMemoryCache cache) => _cache = cache;

    public Task SaveAsync(string key, string value, TimeSpan ttl)
    {
        _cache.Set(key, value, ttl);
        return Task.CompletedTask;
    }

    public Task<string?> GetAndDeleteAsync(string key)
    {
        if (!_cache.TryGetValue(key, out string? value))
            return Task.FromResult<string?>(null);

        _cache.Remove(key);
        return Task.FromResult(value);
    }
}
