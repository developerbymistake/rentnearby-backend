using Microsoft.Extensions.Caching.Memory;
using RentNearBy.Core.Interfaces.Buddy;
using StackExchange.Redis;

namespace RentNearBy.Infrastructure.Services.Buddy.Be1;

public static class BuddyFoundMarkKeys
{
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(15);

    public static string For(Guid finderId, Guid personId) => $"buddy:found:{finderId:N}:{personId:N}";
}

public sealed class RedisBuddyFoundMarks(IConnectionMultiplexer redis) : IBuddyFoundMarks
{
    public Task MarkAsync(Guid finderId, Guid personId)
        => redis.GetDatabase().StringSetAsync(BuddyFoundMarkKeys.For(finderId, personId), 1, BuddyFoundMarkKeys.Ttl);

    public Task<bool> HasAsync(Guid finderId, Guid personId)
        => redis.GetDatabase().KeyExistsAsync(BuddyFoundMarkKeys.For(finderId, personId));
}

public sealed class MemoryBuddyFoundMarks(IMemoryCache cache) : IBuddyFoundMarks
{
    public Task MarkAsync(Guid finderId, Guid personId)
    {
        cache.Set(BuddyFoundMarkKeys.For(finderId, personId), true, BuddyFoundMarkKeys.Ttl);
        return Task.CompletedTask;
    }

    public Task<bool> HasAsync(Guid finderId, Guid personId)
        => Task.FromResult(cache.TryGetValue(BuddyFoundMarkKeys.For(finderId, personId), out _));
}
