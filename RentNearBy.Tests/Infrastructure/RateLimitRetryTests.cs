using RentNearBy.Infrastructure.Services;

namespace RentNearBy.Tests.Infrastructure;

public class RateLimitRetryTests
{
    [Theory]
    [InlineData(400L, 1)]
    [InlineData(1L, 1)]
    [InlineData(0L, 1)]
    [InlineData(-5L, 1)]
    [InlineData(1000L, 1)]
    [InlineData(1001L, 2)]
    [InlineData(1900L, 2)]
    [InlineData(3_589_000L, 3589)]
    [InlineData(3_599_999L, 3600)]
    public void WholeSeconds_RoundsUp_NeverZero(long retryMs, int expectedSeconds)
    {
        var retry = RateLimitRetry.WholeSeconds(retryMs);

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), retry);
        Assert.Equal(expectedSeconds, (int)retry.TotalSeconds);
    }

    [Fact]
    public async Task Denied_SubSecondWait_ReportsOneSecond()
    {
        const long hour = 3_600_000L;
        long now = 0;
        var limiter = new InMemoryRateLimitService(() => now);
        var window = TimeSpan.FromHours(1);
        for (var i = 0; i < 3; i++) Assert.True((await limiter.CheckAsync("otp:verify", 3, window)).IsAllowed);

        now = hour - 400;
        var denied = await limiter.CheckAsync("otp:verify", 3, window);

        Assert.False(denied.IsAllowed);
        Assert.Equal(1, (int)denied.RetryAfter!.Value.TotalSeconds);
    }
}
