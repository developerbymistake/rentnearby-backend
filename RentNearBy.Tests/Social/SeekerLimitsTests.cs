using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Services.Social;

namespace RentNearBy.Tests.Social;

// Contract 4: seeker numbers from the Seeker plans (free / plus / payment off / held / missing key).
public class SeekerLimitsTests
{
    private static CatalogPlan Plan(Guid id, bool isDefault, Func<string, int> value, bool enabled = true, IEnumerable<string>? keys = null)
    {
        var features = (keys ?? SeekerPlanKeys.All).ToDictionary(k => k, value);
        return new CatalogPlan(id, PlanFeatures.Seeker, isDefault ? SeekerPlanSeed.FreeCode : SeekerPlanSeed.PlusCode, isDefault ? "Free" : SeekerPlanSeed.PlusName,
            null, isDefault, enabled, 0, isDefault ? 0 : 1, features, PlanFeatureValues.From(features), Array.Empty<CatalogPrice>(),
            isDefault ? null : SeekerPlanSeed.PlayProductId);
    }

    private static readonly CatalogPlan Free = Plan(SeekerPlanSeed.FreePlanId, true, SeekerPlanKeys.FreeDefault);
    private static readonly CatalogPlan Plus = Plan(SeekerPlanSeed.PlusPlanId, false, SeekerPlanKeys.PlusDefault);

    [Fact]
    public void Free_seeker_gets_the_free_numbers_and_can_upgrade()
    {
        var l = SeekerLimitsService.Compute([Free, Plus], null, paymentOn: true, held: false);
        Assert.Equal((1, 1, 1, 3, false, false), (l.ActiveTrips, l.PublicTrips, l.JoinRequestsPerMonth, l.NewChatsPerMonth, l.GenderOnlyTrips, l.NearMe));
        Assert.False(l.IsPlus);
        Assert.True(l.CanUpgrade);
    }

    [Fact]
    public void Plus_seeker_gets_the_plus_numbers()
    {
        var l = SeekerLimitsService.Compute([Free, Plus], SeekerPlanSeed.PlusPlanId, paymentOn: true, held: false);
        Assert.Equal((6, 3, 5, 10, true, true), (l.ActiveTrips, l.PublicTrips, l.JoinRequestsPerMonth, l.NewChatsPerMonth, l.GenderOnlyTrips, l.NearMe));
        Assert.True(l.IsPlus);
        Assert.False(l.CanUpgrade);
    }

    [Fact]
    public void Payment_off_gives_every_seeker_the_plus_numbers_without_plus()
    {
        var l = SeekerLimitsService.Compute([Free, Plus], null, paymentOn: false, held: false);
        Assert.Equal((6, 3, 5, 10, true, true), (l.ActiveTrips, l.PublicTrips, l.JoinRequestsPerMonth, l.NewChatsPerMonth, l.GenderOnlyTrips, l.NearMe));
        Assert.False(l.IsPlus);
        Assert.False(l.CanUpgrade);
    }

    [Fact]
    public void Held_account_gets_the_free_numbers_even_with_plus()
    {
        var l = SeekerLimitsService.Compute([Free, Plus], SeekerPlanSeed.PlusPlanId, paymentOn: true, held: true);
        Assert.Equal((1, 1, 1, 3), (l.ActiveTrips, l.PublicTrips, l.JoinRequestsPerMonth, l.NewChatsPerMonth));
        Assert.False(l.IsPlus);
        Assert.False(l.CanUpgrade);                  // he already has Plus: never asked to buy it
        var free = SeekerLimitsService.Compute([Free, Plus], null, paymentOn: true, held: true);
        Assert.True(free.CanUpgrade);
        Assert.True(SeekerLimitsService.Compute([Free, Plus], SeekerPlanSeed.FreePlanId, paymentOn: true, held: true).CanUpgrade);
    }

    [Fact]
    public void Missing_key_reads_its_free_default()
    {
        var partial = Plan(SeekerPlanSeed.PlusPlanId, false, SeekerPlanKeys.PlusDefault, keys: [SeekerPlanKeys.ActiveTrips]);
        var l = SeekerLimitsService.Compute([Free, partial], SeekerPlanSeed.PlusPlanId, paymentOn: true, held: false);
        Assert.Equal(6, l.ActiveTrips);
        Assert.Equal(SeekerPlanKeys.FreeDefault(SeekerPlanKeys.NewChatsPerMonth), l.NewChatsPerMonth);
        Assert.False(l.NearMe);
    }

    [Fact]
    public void No_seeker_plans_at_all_reads_the_free_defaults()
    {
        var l = SeekerLimitsService.Compute([], null, paymentOn: true, held: false);
        Assert.Equal((1, 1, 1, 3), (l.ActiveTrips, l.PublicTrips, l.JoinRequestsPerMonth, l.NewChatsPerMonth));
        Assert.False(l.CanUpgrade);
    }

    [Fact]
    public void Seed_numbers_match_the_spec_table()
    {
        Assert.Equal(1, SeekerPlanKeys.FreeDefault(SeekerPlanKeys.ActiveTrips));
        Assert.Equal(6, SeekerPlanKeys.PlusDefault(SeekerPlanKeys.ActiveTrips));   // I-3 (2026-11-28): was 10
        Assert.Equal(3, SeekerPlanKeys.PlusDefault(SeekerPlanKeys.PublicTrips));
        Assert.Equal(5, SeekerPlanKeys.PlusDefault(SeekerPlanKeys.JoinRequestsPerMonth));
        Assert.Equal(10, SeekerPlanKeys.PlusDefault(SeekerPlanKeys.NewChatsPerMonth));
        Assert.Equal((200, 500, 1800), (SeekerPlanSeed.Price1m, SeekerPlanSeed.Price3m, SeekerPlanSeed.Price12m));
        Assert.Equal("seeker_plus", SeekerPlanSeed.PlayProductId);
        Assert.Equal(PlanFeatures.Seeker, UserRoles.PlanFeatureOf(UserRoles.SeekerId));
        Assert.Equal(ListingKinds.Stay, UserRoles.PlanFeatureOf(UserRoles.StayOwnerId));
        Assert.Null(UserRoles.ListingKindOf(UserRoles.SeekerId));
        Assert.DoesNotContain(PlanFeatures.Seeker, PlanFeatures.All);
        Assert.Contains(PlanFeatures.Seeker, PlanFeatures.Purchasable);
    }
}
