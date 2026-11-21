using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Infrastructure.Services.Owner;
using RentNearBy.Tests.Support.Owner;

namespace RentNearBy.Tests.Owner.Foundation;

// Contract 3.1: payment ON = the plan's values; payment OFF = Plus numbers for every limit key, Free for every ranking key,
// a grant keeps its own values (Max), CanUpgrade false. PlanKeySql renders the same numbers for SQL.
public class PlanEntitlementComputeTests
{
    private static PlanEntitlementService.ActiveRow Row(Guid planId, string source = UserPlanSources.Prepaid)
        => new(Guid.NewGuid(), Guid.NewGuid(), planId, source, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(10));

    private static PlanLimits Compute(string kind, bool paymentOn, Guid? planId = null, string source = UserPlanSources.Prepaid, PlanCatalogSnapshot? snapshot = null)
        => PlanEntitlementService.Compute(snapshot ?? OwnerPlanCatalog.Seeded(), paymentOn ? PaymentFeatureStates.On() : PaymentFeatureStates.Off(),
            kind, planId is { } id ? Row(id, source) : null);

    private static IEnumerable<PlanKeyInfo> Keys(string kind, PlanKeyKind keyKind) => PlanKeyCatalog.For(kind).Where(k => k.Kind == keyKind);

    [Theory]
    [InlineData(ListingKinds.Stay)]
    [InlineData(ListingKinds.Taxi)]
    public void Payment_on_without_a_plan_gives_free_values_and_can_upgrade(string kind)
    {
        var limits = Compute(kind, paymentOn: true);
        Assert.All(PlanKeyCatalog.For(kind), k => Assert.Equal(k.SeedFor(kind)!.Free, limits.Get(k.Key)));
        Assert.False(limits.IsPaid);
        Assert.True(limits.CanUpgrade);
        Assert.True(limits.IsDefaultPlan);
    }

    [Theory]
    [InlineData(ListingKinds.Stay, UserPlanSources.Prepaid)]
    [InlineData(ListingKinds.Taxi, UserPlanSources.Subscription)]
    [InlineData(ListingKinds.Stay, UserPlanSources.Trial)]
    [InlineData(ListingKinds.Taxi, UserPlanSources.AdminGrant)]
    public void Payment_on_with_plus_trial_or_grant_gives_plus_values_and_no_upgrade(string kind, string source)
    {
        var plusId = kind == ListingKinds.Stay ? OwnerPlanCatalog.StayPlusId : OwnerPlanCatalog.TaxiPlusId;
        var limits = Compute(kind, paymentOn: true, plusId, source);
        Assert.All(PlanKeyCatalog.For(kind), k => Assert.Equal(k.SeedFor(kind)!.Plus, limits.Get(k.Key)));
        Assert.True(limits.IsPaid);
        Assert.False(limits.CanUpgrade);
    }

    [Theory]
    [InlineData(ListingKinds.Stay)]
    [InlineData(ListingKinds.Taxi)]
    public void Payment_off_gives_plus_for_limit_keys_and_free_for_ranking_keys(string kind)
    {
        var limits = Compute(kind, paymentOn: false);
        Assert.All(Keys(kind, PlanKeyKind.Limit), k => Assert.Equal(k.SeedFor(kind)!.Plus, limits.Get(k.Key)));
        Assert.All(Keys(kind, PlanKeyKind.Ranking), k => Assert.Equal(k.SeedFor(kind)!.Free, limits.Get(k.Key)));
        Assert.Equal(0, limits.PriorityRank);
        Assert.False(limits.IsFeatured);
        Assert.Equal(1, limits.RingWeight);
        Assert.False(limits.IsPaid);
        Assert.False(limits.CanUpgrade);
    }

    [Fact]
    public void Payment_off_changes_shipped_caps_as_the_contract_says()
    {
        var stay = Compute(ListingKinds.Stay, paymentOn: false);
        var taxi = Compute(ListingKinds.Taxi, paymentOn: false);
        Assert.Equal((4, 8, 6), (stay.JobsPosts, stay.Photos, stay.RoomTypes));
        Assert.Equal(5, taxi.Routes);
        Assert.Equal((50, 10, 183, 6, 5), (stay.CalendarEntries, stay.BlockedRanges, stay.UpcomingDays, stay.MonthsAhead, stay.SpecialPrices));
        Assert.True(stay.MonthSummary && stay.Reminders);
    }

    [Theory]
    [InlineData(ListingKinds.Stay)]
    [InlineData(ListingKinds.Taxi)]
    public void Payment_off_with_a_grant_keeps_the_grant_ranking(string kind)
    {
        var plusId = kind == ListingKinds.Stay ? OwnerPlanCatalog.StayPlusId : OwnerPlanCatalog.TaxiPlusId;
        var limits = Compute(kind, paymentOn: false, plusId, UserPlanSources.AdminGrant);
        Assert.Equal(1, limits.PriorityRank);
        Assert.True(limits.IsFeatured);
        if (kind == ListingKinds.Taxi) Assert.Equal(2, limits.RingWeight);
        Assert.All(Keys(kind, PlanKeyKind.Limit), k => Assert.Equal(k.SeedFor(kind)!.Plus, limits.Get(k.Key)));
        Assert.False(limits.IsPaid);
        Assert.False(limits.CanUpgrade);
    }

    [Fact]
    public void Payment_off_takes_limits_only_from_enabled_plans()
    {
        var limits = Compute(ListingKinds.Stay, paymentOn: false, snapshot: OwnerPlanCatalog.Seeded(plusEnabled: false));
        Assert.All(Keys(ListingKinds.Stay, PlanKeyKind.Limit), k => Assert.Equal(k.Stay!.Free, limits.Get(k.Key)));
    }

    [Fact]
    public void Payment_off_keeps_the_free_mode_listing_limit_rule()
    {
        var limits = PlanEntitlementService.Compute(OwnerPlanCatalog.Seeded(), PaymentFeatureStates.Off(freeLimit: 3), ListingKinds.Stay, null);
        Assert.Equal(3, limits.ListingLimit);
    }

    [Fact]
    public void Empty_catalog_falls_back_to_the_free_column()
    {
        var limits = Compute(ListingKinds.Taxi, paymentOn: true, snapshot: new PlanCatalogSnapshot([]));
        Assert.Equal((1, 1, 4, 15, 0), (limits.ListingLimit, limits.Routes, limits.CalendarEntries, limits.UpcomingDays, limits.MonthsAhead));
        Assert.Equal(1, limits.RingWeight);
        Assert.False(limits.Reminders);
    }

    [Fact]
    public void Values_above_the_hard_ceiling_are_clamped()
    {
        var f = OwnerPlanCatalog.Features(ListingKinds.Stay, plus: true);
        f[PlanFeatureKeys.Photos] = 40;
        f[PlanFeatureKeys.RoomTypes] = 20;
        var snapshot = new PlanCatalogSnapshot([OwnerPlanCatalog.Plan(ListingKinds.Stay, false), OwnerPlanCatalog.Plan(ListingKinds.Stay, true, features: f)]);
        var limits = Compute(ListingKinds.Stay, paymentOn: true, OwnerPlanCatalog.StayPlusId, snapshot: snapshot);
        Assert.Equal((OwnerLimits.PhotosCeiling, OwnerLimits.RoomsCeiling), (limits.Photos, limits.RoomTypes));
    }

    [Theory]
    [InlineData(ListingKinds.Stay, true)]
    [InlineData(ListingKinds.Stay, false)]
    [InlineData(ListingKinds.Taxi, true)]
    [InlineData(ListingKinds.Taxi, false)]
    public void Plan_key_sql_renders_what_compute_returns_for_every_plan_and_key(string kind, bool paymentOn)
    {
        var snapshot = OwnerPlanCatalog.Seeded();
        var flag = paymentOn ? PaymentFeatureStates.On() : PaymentFeatureStates.Off();
        var cte = PlanKeySql.Cte(snapshot, flag, kind);
        Assert.StartsWith("pv (\"PlanId\", \"ListingLimit\"", cte);
        Assert.Contains($"'{Guid.Empty}'::uuid", cte);

        var noPlan = paymentOn ? snapshot.DefaultValues(kind) : PlanFeatureValues.OffValues(snapshot, kind);
        var none = Compute(kind, paymentOn, snapshot: snapshot);
        Assert.All(PlanFeatureKeys.All, k => Assert.Equal(none.Get(k), PlanKeySql.Value(noPlan, flag, k)));

        foreach (var plan in snapshot.PlansOf(kind))
        {
            Assert.Contains($"'{plan.Id}'::uuid", cte);
            var values = paymentOn ? plan.Values : PlanFeatureValues.Max(noPlan, plan.Values);
            var limits = Compute(kind, paymentOn, plan.Id, snapshot: snapshot);
            Assert.All(PlanFeatureKeys.All, k => Assert.Equal(limits.Get(k), PlanKeySql.Value(values, flag, k)));
        }
        Assert.Equal("pv.\"PlanId\" = COALESCE(up.\"PlanId\", '00000000-0000-0000-0000-000000000000'::uuid)", PlanKeySql.Join("up"));
        Assert.Equal("pv.\"Reminders\"", PlanKeySql.Column(PlanFeatureKeys.Reminders));
    }
}
