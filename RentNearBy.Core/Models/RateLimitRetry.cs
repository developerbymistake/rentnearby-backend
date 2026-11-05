namespace RentNearBy.Core.Models;

// Retry-After is whole seconds: a denied call's wait is rounded UP (never 0), so (int)TotalSeconds at a caller is exact
// and a client that honours the header does not retry before the permit is free.
public static class RateLimitRetry
{
    public static TimeSpan WholeSeconds(long retryMs) => TimeSpan.FromSeconds(Math.Max(1L, (Math.Max(0L, retryMs) + 999L) / 1000L));
}
