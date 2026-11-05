namespace RentNearBy.Core.Interfaces;

// Token: set on an allowed call; pass it to ReleaseAsync when the work behind the check failed.
public record RateLimitResult(bool IsAllowed, int RemainingAttempts, TimeSpan? RetryAfter, string? Token = null);

public interface IRateLimitService
{
    // Sliding window (SlidingWindowLog): allowed iff the permits taken in the last `window` plus `permits` stay within
    // maxAttempts. A denied call takes nothing.
    Task<RateLimitResult> CheckAsync(string key, int maxAttempts, TimeSpan window, int permits = 1);

    // Gives back the permits of an allowed call (its Token). Unknown or expired tokens are a no-op.
    Task ReleaseAsync(string key, string token);
}
