namespace RentNearBy.Core.Interfaces;

public record RateLimitResult(bool IsAllowed, int RemainingAttempts, TimeSpan? RetryAfter);

public interface IRateLimitService
{
    Task<RateLimitResult> CheckAsync(string key, int maxAttempts, TimeSpan window);
}
