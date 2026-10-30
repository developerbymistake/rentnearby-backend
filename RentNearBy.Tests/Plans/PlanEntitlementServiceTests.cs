using RentNearBy.Infrastructure.Services;

namespace RentNearBy.Tests.Plans;

public class PlanEntitlementServiceTests
{
    private static readonly PlanCatalogSnapshot Catalog = new PlanCatalogSnapshotBuilder().Build();

    private static PlanLimits Compute(PaymentFeatureState flag, PlanEntitlementService.ActiveRow? row, PlanCatalogSnapshot? catalog = null)
        => PlanEntitlementService.Compute(catalog ?? Catalog, flag, ListingKinds.Room, row);

    [Fact]
    public void Compute_PaymentOnNoRow_DefaultPlan()
    {
        var limits = Compute(PaymentFeatureStates.On(), null);

        Assert.Equal(TestIds.RoomFree, limits.PlanId);
        Assert.Equal("Free", limits.PlanName);
        Assert.True(limits.IsDefaultPlan);
        Assert.Null(limits.UserPlanId);
        Assert.False(limits.IsPaid);
        Assert.Null(limits.Source);
        Assert.Null(limits.StartsAt);
        Assert.Null(limits.EndsAt);
        Assert.Equal(1, limits.ListingLimit);
        Assert.Equal((short)0, limits.PriorityRank);
        Assert.False(limits.IsFeatured);
        Assert.Equal(0, limits.JobsPosts);
        Assert.True(limits.PaymentEnabled);
        Assert.Equal(30, limits.FreeDays);
    }

    [Fact]
    public void Compute_PaymentOnPlusRow_PlusFeaturesAndRowEchoed()
    {
        var startsAt = Dates.Utc(2026, 10, 1);
        var row = new ActiveRowBuilder().WithSource(UserPlanSources.Prepaid).Between(startsAt, Dates.Far).Build();

        var limits = Compute(PaymentFeatureStates.On(), row);

        Assert.Equal(TestIds.RoomPlus, limits.PlanId);
        Assert.Equal("Plus", limits.PlanName);
        Assert.False(limits.IsDefaultPlan);
        Assert.Equal(row.Id, limits.UserPlanId);
        Assert.True(limits.IsPaid);
        Assert.Equal(UserPlanSources.Prepaid, limits.Source);
        Assert.Equal(startsAt, limits.StartsAt);
        Assert.Equal(Dates.Far, limits.EndsAt);
        Assert.Equal(3, limits.ListingLimit);
        Assert.Equal((short)2, limits.PriorityRank);
        Assert.True(limits.IsFeatured);
        Assert.Equal(2, limits.JobsPosts);
    }

    [Fact]
    public void Compute_PaymentOffNoRow_FreeModeLimitRaisesDefault()
    {
        var limits = Compute(PaymentFeatureStates.Off(freeLimit: 5), null);

        Assert.Equal(5, limits.ListingLimit);
        Assert.False(limits.IsPaid);
        Assert.False(limits.PaymentEnabled);
        Assert.True(limits.IsDefaultPlan);
    }

    [Fact]
    public void Compute_PaymentOffPlusRow_MaxOfDefaultPlanAndFreeLimitButNotPaid()
    {
        var limits = Compute(PaymentFeatureStates.Off(freeLimit: 2), new ActiveRowBuilder().Build());

        Assert.Equal(3, limits.ListingLimit);
        Assert.Equal((short)2, limits.PriorityRank);
        Assert.True(limits.IsFeatured);
        Assert.False(limits.IsPaid);
        Assert.Equal("Plus", limits.PlanName);
        Assert.False(limits.IsDefaultPlan);
    }

    [Fact]
    public void Compute_PaymentOffPlanBelowDefault_KeepsDefaultLimit()
    {
        var catalog = new PlanCatalogSnapshotBuilder().With(
            PlanCatalogSnapshotBuilder.RoomFree,
            CatalogPlanBuilder.Plus().WithListingLimit(0).Build()).Build();

        var limits = Compute(PaymentFeatureStates.Off(), new ActiveRowBuilder().Build(), catalog);

        Assert.Equal(1, limits.ListingLimit);
    }

    [Fact]
    public void Compute_PaymentOnPlanBelowDefault_UsesPlanAsIs()
    {
        var catalog = new PlanCatalogSnapshotBuilder().With(
            PlanCatalogSnapshotBuilder.RoomFree,
            CatalogPlanBuilder.Plus().WithListingLimit(0).Build()).Build();

        var limits = Compute(PaymentFeatureStates.On(), new ActiveRowBuilder().Build(), catalog);

        Assert.Equal(0, limits.ListingLimit);
    }

    [Fact]
    public void Compute_RowPlanMissingFromCatalog_FallsBackToDefault()
    {
        var limits = Compute(PaymentFeatureStates.On(), new ActiveRowBuilder().OnPlan(Guid.NewGuid()).Build());

        Assert.True(limits.IsDefaultPlan);
        Assert.Null(limits.UserPlanId);
        Assert.False(limits.IsPaid);
        Assert.Equal(TestIds.RoomFree, limits.PlanId);
        Assert.Equal(1, limits.ListingLimit);
    }

    [Fact]
    public void Compute_NoDefaultPlanNoRow_FallbackValues()
    {
        var limits = Compute(PaymentFeatureStates.On(), null, new PlanCatalogSnapshotBuilder().Empty().Build());

        Assert.Equal(Guid.Empty, limits.PlanId);
        Assert.Equal("Free", limits.PlanName);
        Assert.Equal(1, limits.ListingLimit);
        Assert.Equal((short)0, limits.PriorityRank);
        Assert.False(limits.IsFeatured);
        Assert.Equal(1, limits.JobsPosts);
    }

    [Theory]
    [InlineData(70000, 32767)]
    [InlineData(-5, 0)]
    [InlineData(9, 9)]
    public void Compute_Priority_ClampedToSmallint(int priority, int expectedRank)
    {
        var catalog = new PlanCatalogSnapshotBuilder().With(
            PlanCatalogSnapshotBuilder.RoomFree,
            CatalogPlanBuilder.Plus().WithPriority(priority).Build()).Build();

        var limits = Compute(PaymentFeatureStates.On(), new ActiveRowBuilder().Build(), catalog);

        Assert.Equal((short)expectedRank, limits.PriorityRank);
    }

    [Fact]
    public void Compute_NegativeLimits_ClampedToZero()
    {
        var catalog = new PlanCatalogSnapshotBuilder().With(
            PlanCatalogSnapshotBuilder.RoomFree,
            CatalogPlanBuilder.Plus().WithJobsPosts(-1).WithListingLimit(-4).Build()).Build();

        var limits = Compute(PaymentFeatureStates.On(), new ActiveRowBuilder().Build(), catalog);

        Assert.Equal(0, limits.JobsPosts);
        Assert.Equal(0, limits.ListingLimit);
    }

    private static readonly PlanEntitlementService Service = new(null!, null!, null!);

    private static PlanLimits Limits(bool paymentEnabled, int freeDays, DateTime? endsAt)
        => new(TestIds.RoomPlus, "Plus", false, TestIds.UserPlan1, paymentEnabled, UserPlanSources.Subscription,
            Dates.Past, endsAt, 3, 2, true, 2, paymentEnabled, freeDays);

    private static void AssertAbout(DateTime expected, DateTime actual)
        => Assert.InRange(actual, expected.AddSeconds(-5), expected.AddSeconds(5));

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(30, 30)]
    public void ResolveValidUntil_NoEndsAt_NowPlusAtLeastOneFreeDay(int freeDays, int expectedDays)
        => AssertAbout(DateTime.UtcNow.AddDays(expectedDays), Service.ResolveValidUntil(Limits(true, freeDays, null)));

    [Fact]
    public void ResolveValidUntil_PaymentOnWithEndsAt_ExactEndsAt()
    {
        var endsAt = DateTime.UtcNow.AddDays(3);

        Assert.Equal(endsAt, Service.ResolveValidUntil(Limits(true, 30, endsAt)));
    }

    [Fact]
    public void ResolveValidUntil_PaymentOffEndsBeforeFreeWindow_FreeWindow()
        => AssertAbout(DateTime.UtcNow.AddDays(30), Service.ResolveValidUntil(Limits(false, 30, DateTime.UtcNow.AddDays(3))));

    [Fact]
    public void ResolveValidUntil_PaymentOffEndsAfterFreeWindow_EndsAt()
    {
        var endsAt = DateTime.UtcNow.AddDays(90);

        Assert.Equal(endsAt, Service.ResolveValidUntil(Limits(false, 30, endsAt)));
    }
}
