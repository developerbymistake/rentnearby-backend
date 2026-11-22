using RentNearBy.Infrastructure.Services;
using RentNearBy.Tests.Owner.Be3;
using RentNearBy.Tests.Support.Owner;
using RentNearBy.Tests.Trips;

namespace RentNearBy.Tests.Owner.Journeys;

// FB-14 (D-24, JT-B-LC-3) and FB-15 (D-10, JT-B-TX-1 step Grace): payment OFF ranking and the entitled-row predicate.
public class PaymentOffRankingTests
{
    [Theory]
    [InlineData(ListingKinds.Stay, "Trial")]
    [InlineData(ListingKinds.Stay, "Subscription")]
    [InlineData(ListingKinds.Taxi, "Prepaid")]
    [InlineData(ListingKinds.Taxi, "Subscription")]
    public void While_off_a_non_grant_row_keeps_plus_limits_and_gets_free_ranking(string kind, string source)
    {
        var limits = Be3Kit.Compute(kind, false, Be3Kit.PlusId(kind), source);
        var free = Be3Kit.Off(kind);
        var plus = Be3Kit.Plus(kind);
        Assert.Equal((free.PriorityRank, free.IsFeatured, free.RingWeight), (limits.PriorityRank, limits.IsFeatured, limits.RingWeight));
        Assert.Equal(plus.Photos, Math.Min(limits.Photos, plus.Photos));
        Assert.True(limits.Routes >= plus.Routes && limits.CalendarEntries >= plus.CalendarEntries);
    }

    [Theory]
    [InlineData(ListingKinds.Stay)]
    [InlineData(ListingKinds.Taxi)]
    public void While_off_an_admin_grant_raises_ranking(string kind)
    {
        var grant = Be3Kit.OffWithGrant(kind);
        var plus = Be3Kit.Plus(kind);
        Assert.Equal((plus.PriorityRank, plus.IsFeatured, plus.RingWeight), (grant.PriorityRank, grant.IsFeatured, grant.RingWeight));
    }

    [Fact]
    public void Play_row_while_off_writes_ring_weight_one()
        => Assert.Equal(1, Be3Kit.Compute(ListingKinds.Taxi, false, OwnerPlanCatalog.TaxiPlusId, UserPlanSources.Subscription).RingWeight);

    [Fact]
    public void Entitled_row_includes_a_play_row_past_its_end()
    {
        var rule = PlanRowSql.Entitled(DateTime.UtcNow).Compile();
        var play = new UserPlan { Status = UserPlanStatuses.Active, EndsAt = DateTime.UtcNow.AddMinutes(-10), PlaySubscriptionId = Guid.NewGuid() };
        var prepaid = new UserPlan { Status = UserPlanStatuses.Active, EndsAt = DateTime.UtcNow.AddMinutes(-10) };
        var cancelled = new UserPlan { Status = UserPlanStatuses.Cancelled, EndsAt = DateTime.UtcNow.AddDays(3), PlaySubscriptionId = Guid.NewGuid() };
        Assert.True(rule(play));
        Assert.False(rule(prepaid));
        Assert.False(rule(cancelled));
        Assert.Equal("u.\"Status\" = 'Active' AND (u.\"EndsAt\" > now() OR u.\"PlaySubscriptionId\" IS NOT NULL)", PlanRowSql.EntitledSql("u"));
    }

    [Fact]
    public void No_other_plan_predicate_ends_a_play_row_by_the_clock()
    {
        var root = CompletionCounterGuardTests.Root();
        string[] files =
        [
            Path.Combine(root, "RentNearBy.Infrastructure", "Services", "PlanEntitlementService.cs"),
            Path.Combine(root, "RentNearBy.Infrastructure", "Services", "PlanSlotService.cs"),
            Path.Combine(root, "RentNearBy.Infrastructure", "Services", "Owner", "Be3", "PlanLimitsReconcile.cs"),
            Path.Combine(root, "RentNearBy.Infrastructure", "Services", "Owner", "Be4", "OwnerJobSql.cs"),
            Path.Combine(root, "RentNearBy.Infrastructure", "Services", "Owner", "Be4", "StayOwnerHomeReader.cs"),
            Path.Combine(root, "RentNearBy.Infrastructure", "Services", "Owner", "Be4", "TaxiDriverHomeReader.cs"),
            Path.Combine(root, "RentNearBy.Infrastructure", "Services", "Owner", "OwnerContextReader.cs"),
        ];
        foreach (var f in files)
        {
            var src = File.ReadAllText(f);
            Assert.DoesNotContain("\"EndsAt\" > now()", src);
            Assert.DoesNotContain("u.EndsAt > now)", src);
        }
    }
}
