using RentNearBy.Infrastructure.Services;

namespace RentNearBy.Tests.Infrastructure;

public class RateLimitPermitsTests
{
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);
    private static readonly TimeSpan Day = TimeSpan.FromHours(24);

    [Fact]
    public async Task FailedOtpSends_GiveThePermitBack()
    {
        var limiter = new InMemoryRateLimitService(() => 0);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var sends = new RateLimitPermits(limiter);
            Assert.True((await sends.TakeAsync("otp:send:login:9000000001", 2, Hour)).IsAllowed);
            await sends.ReleaseAllAsync();
        }

        var afterRecovery = new RateLimitPermits(limiter);
        Assert.True((await afterRecovery.TakeAsync("otp:send:login:9000000001", 2, Hour)).IsAllowed);
        Assert.True((await afterRecovery.TakeAsync("otp:send:login:9000000001", 2, Hour)).IsAllowed);
    }

    [Fact]
    public async Task LeadLimitDenial_DoesNotSpendAskPermits()
    {
        var limiter = new InMemoryRateLimitService(() => 0);
        for (var i = 0; i < 10; i++) Assert.True((await limiter.CheckAsync("trip:lead:t1", 10, Day)).IsAllowed);

        for (var tap = 0; tap < 3; tap++)
        {
            var quotas = new RateLimitPermits(limiter);
            Assert.True((await quotas.TakeAsync("trip:enquiry:h:u1", 20, Hour)).IsAllowed);
            Assert.True((await quotas.TakeAsync("trip:enquiry:d:u1", 60, Day)).IsAllowed);
            Assert.False((await quotas.TakeAsync("trip:lead:t1", 10, Day)).IsAllowed);
            Assert.Equal(0, quotas.Held);
        }

        var probe = await limiter.CheckAsync("trip:enquiry:h:u1", 20, Hour);
        Assert.Equal(19, probe.RemainingAttempts);
        Assert.Equal(59, (await limiter.CheckAsync("trip:enquiry:d:u1", 60, Day)).RemainingAttempts);
    }

    [Fact]
    public async Task CommittedWork_KeepsPermits()
    {
        var limiter = new InMemoryRateLimitService(() => 0);
        var quotas = new RateLimitPermits(limiter);
        Assert.True((await quotas.TakeAsync("k", 1, Hour)).IsAllowed);
        Assert.Equal(1, quotas.Held);

        Assert.False((await limiter.CheckAsync("k", 1, Hour)).IsAllowed);
    }

    [Fact]
    public async Task ReleaseAll_Twice_IsANoOp()
    {
        var limiter = new InMemoryRateLimitService(() => 0);
        var quotas = new RateLimitPermits(limiter);
        Assert.True((await quotas.TakeAsync("k", 1, Hour)).IsAllowed);
        await quotas.ReleaseAllAsync();
        Assert.True((await limiter.CheckAsync("k", 1, Hour)).IsAllowed);

        await quotas.ReleaseAllAsync();
        Assert.False((await limiter.CheckAsync("k", 1, Hour)).IsAllowed);
    }

    [Fact]
    public async Task EmptyLease_ReleaseIsSafe()
    {
        var quotas = new RateLimitPermits(new InMemoryRateLimitService(() => 0));
        await quotas.ReleaseAllAsync();
        Assert.Equal(0, quotas.Held);
    }
}
