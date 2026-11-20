using Microsoft.Extensions.Caching.Memory;
using RentNearBy.Core.Interfaces;
using StackExchange.Redis;

namespace RentNearBy.Infrastructure.Services;

public sealed class RedisPendingUsernameStore(IConnectionMultiplexer redis) : IPendingUsernameStore
{
    public async Task<string?> GetAsync(string phoneNumber)
    {
        var value = await redis.GetDatabase().StringGetAsync(PendingUsernameKeys.For(phoneNumber));
        return value.IsNull ? null : (string?)value;
    }

    public Task SaveAsync(string phoneNumber, string username, TimeSpan ttl)
        => redis.GetDatabase().StringSetAsync(PendingUsernameKeys.For(phoneNumber), username, ttl);

    public Task DeleteAsync(string phoneNumber)
        => redis.GetDatabase().KeyDeleteAsync(PendingUsernameKeys.For(phoneNumber));
}

public sealed class MemoryPendingUsernameStore(IMemoryCache cache) : IPendingUsernameStore
{
    public Task<string?> GetAsync(string phoneNumber)
        => Task.FromResult(cache.TryGetValue(PendingUsernameKeys.For(phoneNumber), out string? value) ? value : null);

    public Task SaveAsync(string phoneNumber, string username, TimeSpan ttl)
    {
        cache.Set(PendingUsernameKeys.For(phoneNumber), username, ttl);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string phoneNumber)
    {
        cache.Remove(PendingUsernameKeys.For(phoneNumber));
        return Task.CompletedTask;
    }
}

public static class PendingUsernameKeys
{
    public static string For(string phoneNumber) => $"signup:username:{phoneNumber}";
}
