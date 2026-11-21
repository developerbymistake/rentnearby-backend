using RentNearBy.Core.Models.Owner;

namespace RentNearBy.Tests.Owner.Be3;

// Contract 3.2 JobsPosts, AU-22: the job cap comes from the plan (1 Free, 4 Plus, 4 while payment is OFF), the refusal uses the
// one limit data shape plus "active", and jobs above the cap after Plus ends are never closed (overCap).
public class JobLimitCopyTests
{
    [Fact]
    [PlanKeyTest(PlanFeatureKeys.JobsPosts)]
    public void JobsPosts_SecondOnFree_Refused_WithTheTwoMessageLines()
    {
        var free = Be3Kit.Free(ListingKinds.Stay);
        Assert.Equal(1, free.JobsPosts);
        Assert.True(free.CanUpgrade);
        Assert.Equal("Your plan allows 1 active job. Close one or upgrade.", StayJobRules.LimitMessage(free.JobsPosts, free.CanUpgrade));

        var plus = Be3Kit.Plus(ListingKinds.Stay);
        Assert.Equal(4, plus.JobsPosts);
        Assert.Equal("You can have up to 4 active jobs. Close one to post another.", StayJobRules.LimitMessage(plus.JobsPosts, plus.CanUpgrade));

        var data = StayJobRules.LimitData(free.JobsPosts, active: 1, free.CanUpgrade);
        Assert.Equal((PlanFeatureKeys.JobsPosts, 1, 1, true, 1), (data.Key, data.Limit, data.Used, data.CanUpgrade, data.Active));

        var handler = Be3Kit.Source("RentNearBy.Api", "Handlers", "StayJobHandlers.cs");
        Assert.Contains("if (active >= maxActive)", handler);
        Assert.Contains("StayJobRules.LimitData(maxActive, active, limits.CanUpgrade)", handler);
    }

    [Fact]
    public void Payment_off_cap_is_four_and_never_offers_an_upgrade()
    {
        var off = Be3Kit.Off(ListingKinds.Stay);
        Assert.Equal(4, off.JobsPosts);
        Assert.False(off.CanUpgrade);
        Assert.Equal("You can have up to 4 active jobs. Close one to post another.", StayJobRules.LimitMessage(off.JobsPosts, off.CanUpgrade));
    }

    [Fact]
    public void Over_cap_after_plus_ends_is_reported_and_jobs_stay_open()
    {
        var handler = Be3Kit.Source("RentNearBy.Api", "Handlers", "StayJobHandlers.cs");
        Assert.Contains("OverCap = activeCount > maxActive", handler);
        Assert.Contains("CanUpgrade = limits.CanUpgrade", handler);
        Assert.Contains("PaymentEnabled = limits.PaymentEnabled", handler);
        // 3 active on Free: overCap, canPost false; nothing in the plan code closes a job.
        var free = Be3Kit.Free(ListingKinds.Stay);
        Assert.True(3 > free.JobsPosts);
        Assert.DoesNotContain("StayJob", Be3Kit.Source("RentNearBy.Infrastructure", "Services", "Owner", "Be3", "PlanLimitsReconcile.cs"));
    }
}
