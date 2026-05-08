using System.Collections.Concurrent;
using RentNearBy.Core.Interfaces;

namespace RentNearBy.Infrastructure.Services;

public class InMemoryRateLimitService : IRateLimitService
{
    private record WindowEntry(int Count, DateTimeOffset ExpiresAt);

    private readonly ConcurrentDictionary<string, WindowEntry> _store = new();

    public Task<RateLimitResult> CheckAsync(string key, int maxAttempts, TimeSpan window)
    {
        var now = DateTimeOffset.UtcNow;

        var entry = _store.AddOrUpdate(
            key,
            _ => new WindowEntry(1, now.Add(window)),
            (_, existing) =>
            {
                if (now >= existing.ExpiresAt)
                    return new WindowEntry(1, now.Add(window));
                return existing with { Count = existing.Count + 1 };
            });

        if (entry.Count > maxAttempts)
        {
            var retryAfter = entry.ExpiresAt - now;
            return Task.FromResult(new RateLimitResult(false, 0, retryAfter > TimeSpan.Zero ? retryAfter : window));
        }

        return Task.FromResult(new RateLimitResult(true, maxAttempts - entry.Count, null));
    }
}
