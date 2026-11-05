using RentNearBy.Infrastructure.Services;

namespace RentNearBy.Tests.Infrastructure;

public class SlidingWindowRateLimitTests
{
    private const long Hour = 3_600_000;

    private static long At(int h, int m, int s) => ((h * 60L + m) * 60 + s) * 1000;

    // Audit example, OTP verify 3 per hour: the old fixed window (opened 10:00:00) let 3 more guesses through at 11:00:01.
    [Fact]
    public void Boundary_burst_is_closed()
    {
        var log = new SlidingWindowLog();
        Assert.True(log.TryAcquire(At(10, 0, 0), Hour, 3, 1, "a").Allowed);
        Assert.True(log.TryAcquire(At(10, 59, 50), Hour, 3, 1, "b").Allowed);
        Assert.True(log.TryAcquire(At(10, 59, 50), Hour, 3, 1, "c").Allowed);

        var denied = log.TryAcquire(At(10, 59, 55), Hour, 3, 1, "d");
        Assert.False(denied.Allowed);
        Assert.Equal(5_000, denied.RetryAfterMs);

        Assert.True(log.TryAcquire(At(11, 0, 1), Hour, 3, 1, "e").Allowed);
        var second = log.TryAcquire(At(11, 0, 1), Hour, 3, 1, "f");
        Assert.False(second.Allowed);
        Assert.Equal(At(10, 59, 50) + Hour - At(11, 0, 1), second.RetryAfterMs);
    }

    [Fact]
    public void A_denied_call_takes_nothing()
    {
        var log = new SlidingWindowLog();
        log.TryAcquire(0, 1000, 1, 1, "a");
        for (var i = 0; i < 5; i++) Assert.False(log.TryAcquire(500, 1000, 1, 1, $"x{i}").Allowed);
        Assert.Equal(1, log.Count);
        Assert.True(log.TryAcquire(1000, 1000, 1, 1, "b").Allowed);
    }

    [Fact]
    public void Release_gives_the_permit_back()
    {
        var log = new SlidingWindowLog();
        Assert.True(log.TryAcquire(0, Hour, 2, 1, "send1").Allowed);
        Assert.True(log.TryAcquire(10, Hour, 2, 1, "send2").Allowed);
        Assert.False(log.TryAcquire(20, Hour, 2, 1, "send3").Allowed);

        Assert.Equal(1, log.Release("send2"));
        Assert.Equal(0, log.Release("unknown"));
        Assert.True(log.TryAcquire(30, Hour, 2, 1, "send4").Allowed);
    }

    [Fact]
    public void Permits_count_each_lead_and_never_exceed_the_limit()
    {
        var log = new SlidingWindowLog();
        var first = log.TryAcquire(0, Hour, 10, 4, "a");
        Assert.True(first.Allowed);
        Assert.Equal(6, first.Remaining);
        Assert.Equal(4, log.Count);

        var tooMany = log.TryAcquire(100, Hour, 10, 7, "b");
        Assert.False(tooMany.Allowed);
        Assert.Equal(Hour - 100, tooMany.RetryAfterMs);

        Assert.Equal(Hour, log.TryAcquire(200, Hour, 10, 11, "c").RetryAfterMs);
        Assert.Equal(4, log.Release("a"));
        Assert.True(log.TryAcquire(300, Hour, 10, 10, "d").Allowed);
    }

    [Fact]
    public void Out_of_order_times_stay_sorted()
    {
        var log = new SlidingWindowLog();
        log.TryAcquire(500, 1000, 3, 1, "a");
        log.TryAcquire(400, 1000, 3, 1, "b");
        Assert.Equal(0, log.TryAcquire(600, 1000, 3, 1, "c").Remaining);
        Assert.True(log.TryAcquire(1401, 1000, 3, 1, "d").Allowed);
        Assert.Equal(3, log.Count);
    }

    [Fact]
    public async Task In_memory_service_follows_the_same_rule_with_a_fake_clock()
    {
        long now = At(10, 0, 0);
        var limiter = new InMemoryRateLimitService(() => now);
        var window = TimeSpan.FromHours(1);

        Assert.True((await limiter.CheckAsync("otp:v", 3, window)).IsAllowed);
        now = At(10, 59, 50);
        Assert.True((await limiter.CheckAsync("otp:v", 3, window)).IsAllowed);
        var last = await limiter.CheckAsync("otp:v", 3, window);
        Assert.True(last.IsAllowed);
        Assert.NotNull(last.Token);

        now = At(11, 0, 1);
        Assert.True((await limiter.CheckAsync("otp:v", 3, window)).IsAllowed);
        var denied = await limiter.CheckAsync("otp:v", 3, window);
        Assert.False(denied.IsAllowed);
        Assert.Null(denied.Token);
        Assert.Equal(TimeSpan.FromMilliseconds(At(10, 59, 50) + Hour - At(11, 0, 1)), denied.RetryAfter);

        await limiter.ReleaseAsync("otp:v", last.Token!);
        Assert.True((await limiter.CheckAsync("otp:v", 3, window)).IsAllowed);
        await limiter.ReleaseAsync("missing", "nothing");
    }
}
