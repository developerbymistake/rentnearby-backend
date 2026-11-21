using Microsoft.Extensions.Caching.Memory;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Infrastructure.Services.Owner.Be2;
using RentNearBy.Tests.Support;
using RentNearBy.Tests.Support.Owner;
using RentNearBy.Tests.TaxiRequests;

namespace RentNearBy.Tests.Owner.Be2;

// Contract 3.2 (SpecialPrices): active rules incl. paused and plan-paused >= the plan number refuse P2 with 409 SpecialPriceLimit.
public class SpecialPriceLimitTests
{
    private static PlanLimits Free => PlanEntitlementService.Compute(OwnerPlanCatalog.Seeded(), PaymentFeatureStates.On(), ListingKinds.Stay, null);

    private static PlanLimits Off => PlanEntitlementService.Compute(OwnerPlanCatalog.Seeded(), PaymentFeatureStates.Off(), ListingKinds.Stay, null);

    private static PlanLimits Plus => PlanEntitlementService.Compute(OwnerPlanCatalog.Seeded(), PaymentFeatureStates.On(), ListingKinds.Stay,
        new PlanEntitlementService.ActiveRow(Guid.NewGuid(), Guid.NewGuid(), OwnerPlanCatalog.StayPlusId, UserPlanSources.Prepaid,
            DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(10)));

    [Fact]
    [PlanKeyTest(PlanFeatureKeys.SpecialPrices)]
    public void SpecialPrices_SecondOnFree_Refused_plus_and_off_get_five()
    {
        Assert.Equal(1, Free.SpecialPrices);
        Assert.False(StaySpecialPriceRules.LimitReached(0, Free.SpecialPrices));
        Assert.True(StaySpecialPriceRules.LimitReached(1, Free.SpecialPrices));
        Assert.Equal("1 special price on Free.", OwnerErrorTypes.LimitMessage(OwnerErrorTypes.SpecialPriceLimit, Free.SpecialPrices, Free.CanUpgrade));

        Assert.Equal(5, Plus.SpecialPrices);
        Assert.False(StaySpecialPriceRules.LimitReached(4, Plus.SpecialPrices));
        Assert.True(StaySpecialPriceRules.LimitReached(5, Plus.SpecialPrices));
        Assert.Equal("You can add up to 5 special prices.", OwnerErrorTypes.LimitMessage(OwnerErrorTypes.SpecialPriceLimit, Plus.SpecialPrices, Plus.CanUpgrade));

        Assert.Equal(5, Off.SpecialPrices);
        Assert.False(Off.CanUpgrade);
        Assert.False(StaySpecialPriceRules.LimitReached(4, Off.SpecialPrices));
        Assert.True(StaySpecialPriceRules.LimitReached(5, Off.SpecialPrices));
        Assert.Equal("You can add up to 5 special prices.", OwnerErrorTypes.LimitMessage(OwnerErrorTypes.SpecialPriceLimit, Off.SpecialPrices, Off.CanUpgrade));
        Assert.Equal(409, OwnerErrorTypes.StatusOf(OwnerErrorTypes.SpecialPriceLimit));
    }

    // The service is the rejection point: create asks the rule under the stay lock, and an ended rule (no longer counted) cannot
    // be edited back to life.
    [Fact]
    public void The_service_checks_the_limit_on_create_and_never_edits_an_ended_rule()
    {
        var service = TaxiSource.Read("RentNearBy.Infrastructure", "Services", "Owner", "Be2", "StaySpecialPriceService.cs");
        Assert.Contains("StaySpecialPriceRules.LimitReached(check.Active, ctx.Limits.SpecialPrices)", service);
        Assert.Contains("row.StayListingId != ctx.ListingId || !StaySpecialPriceRules.IsActive(row.DateTo, ctx.Today)", service);
    }

    [Fact]
    public void The_offer_card_the_traveller_reads_carries_no_listed_total()
        => Assert.DoesNotContain("listedStayTotal", TaxiSource.Read("RentNearBy.Api", "Handlers", "TripOwnerHandlers.cs"));
}

public class FestivalCacheKeyTests
{
    [Fact]
    public void Another_spelling_of_a_state_never_shares_its_key()
    {
        var cache = new FestivalCache(new MemoryCache(new MemoryCacheOptions()));
        var today = new DateOnly(2026, 11, 21);
        Assert.NotEqual(cache.Key("Uttarakhand", today), cache.Key("uttarakhand", today));
        Assert.Equal(cache.Key("Uttarakhand", today), cache.Key(" Uttarakhand ", today));
        Assert.NotEqual(cache.Key(null, today), cache.Key("Uttarakhand", today));
    }

    [Fact]
    public void A_write_moves_every_key()
    {
        var cache = new FestivalCache(new MemoryCache(new MemoryCacheOptions()));
        var today = new DateOnly(2026, 11, 21);
        var before = cache.Key("Uttarakhand", today);
        cache.Drop();
        Assert.NotEqual(before, cache.Key("Uttarakhand", today));
    }
}
