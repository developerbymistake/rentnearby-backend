using RentNearBy.Core.Interfaces;

namespace RentNearBy.Core.Models;

// Permits taken by one request across several limits. A request that does not do its work (a later limit denies it,
// the send fails, the transaction rolls back, nothing was written) gives every permit back with ReleaseAllAsync.
// A denied TakeAsync gives back the permits this lease already holds, so one limit never spends another's quota.
public sealed class RateLimitPermits(IRateLimitService limiter)
{
    private readonly List<(string Key, string Token)> _held = [];

    public int Held => _held.Count;

    public async Task<RateLimitResult> TakeAsync(string key, int maxAttempts, TimeSpan window, int permits = 1)
    {
        var result = await limiter.CheckAsync(key, maxAttempts, window, permits);
        if (!result.IsAllowed)
        {
            await ReleaseAllAsync();
            return result;
        }
        if (result.Token is { } token) _held.Add((key, token));
        return result;
    }

    public async Task ReleaseAllAsync()
    {
        for (var i = _held.Count - 1; i >= 0; i--)
            await limiter.ReleaseAsync(_held[i].Key, _held[i].Token);
        _held.Clear();
    }
}
