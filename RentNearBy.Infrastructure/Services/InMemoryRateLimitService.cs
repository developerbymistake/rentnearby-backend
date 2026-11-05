using System.Collections.Concurrent;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;

namespace RentNearBy.Infrastructure.Services;

public class InMemoryRateLimitService : IRateLimitService
{
    private readonly ConcurrentDictionary<string, SlidingWindowLog> _store = new();
    private readonly Func<long> _nowMs;

    public InMemoryRateLimitService() : this(() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
    {
    }

    internal InMemoryRateLimitService(Func<long> nowMs) => _nowMs = nowMs;

    public Task<RateLimitResult> CheckAsync(string key, int maxAttempts, TimeSpan window, int permits = 1)
    {
        var log = _store.GetOrAdd(key, _ => new SlidingWindowLog());
        var token = Guid.NewGuid().ToString("N");
        lock (log)
        {
            var (allowed, remaining, retryMs) = log.TryAcquire(_nowMs(), (long)window.TotalMilliseconds, maxAttempts, permits, token);
            return Task.FromResult(allowed
                ? new RateLimitResult(true, remaining, null, token)
                : new RateLimitResult(false, 0, TimeSpan.FromMilliseconds(retryMs)));
        }
    }

    public Task ReleaseAsync(string key, string token)
    {
        if (_store.TryGetValue(key, out var log))
            lock (log) log.Release(token);
        return Task.CompletedTask;
    }
}
