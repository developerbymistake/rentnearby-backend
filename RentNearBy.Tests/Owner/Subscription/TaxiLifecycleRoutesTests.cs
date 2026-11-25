using RentNearBy.Core.Models.Owner;
using RentNearBy.Tests.Owner.Be3;
using static RentNearBy.Tests.Owner.Subscription.TaxiLifecycleKit;

namespace RentNearBy.Tests.Owner.Subscription;

// Subscription matrix L1-L16 for the taxi Routes key (Free 1, Plus 5). Pure: the same choice the server runs on grant and expiry.
public class TaxiLifecycleRoutesTests
{
    [Fact]
    public void L1_free_below_limit_may_add_the_first_route()
    {
        Assert.Equal(1, Free.Routes);
        Assert.True(PlanRowChoice.SaveAllowed(1, Free.Routes, 0));
    }

    [Fact]
    public void L2_free_at_limit_is_refused_with_the_upgrade_line()
    {
        Assert.False(PlanRowChoice.SaveAllowed(2, Free.Routes, 1));
        Assert.True(Free.CanUpgrade);
        Assert.Equal("1 route on Free.", OwnerErrorTypes.LimitMessage(OwnerErrorTypes.TaxiRouteLimit, Free.Routes, Free.CanUpgrade));
        Assert.Equal(409, OwnerErrorTypes.StatusOf(OwnerErrorTypes.TaxiRouteLimit));
    }

    [Fact]
    public void L3_buy_plus_raises_the_limit_at_once()
    {
        Assert.Equal(5, Plus.Routes);
        Assert.True(PlanRowChoice.SaveAllowed(2, Plus.Routes, 1));
        Assert.True(PlanRowChoice.SaveAllowed(5, Plus.Routes, 1));
    }

    [Fact]
    public void L4_plus_at_limit_is_refused_without_upgrade()
    {
        Assert.False(PlanRowChoice.SaveAllowed(6, Plus.Routes, 5));
        Assert.False(Plus.CanUpgrade);
        Assert.Equal("You can add up to 5 routes.", OwnerErrorTypes.LimitMessage(OwnerErrorTypes.TaxiRouteLimit, Plus.Routes, Plus.CanUpgrade));
    }

    [Fact]
    public void L5_cancel_while_plus_runs_changes_nothing()
    {
        var rows = Apply(Routes(5), Plus);
        Assert.All(rows, r => Assert.False(r.Hidden));
        Assert.Empty(PlanRowChoice.Changed(rows, PlanRowChoice.ChooseIndexed(rows, Plus.Routes, Plus.PaymentEnabled)));
    }

    [Fact]
    public void L6_expire_keeps_the_last_added_route_and_hides_the_rest_without_deleting()
    {
        var start = Routes(5);
        var after = Apply(start, Free);

        Assert.Equal(5, after.Count);
        Assert.Equal([start[4].Id], VisibleInOrder(after));
        Assert.Equal(start.Take(4).Select(r => r.Id).ToList(), HiddenInOrder(after));
        Assert.All(after.Where(r => r.Hidden), r => Assert.True(r.Index > PlanRowBands.HiddenBase));
        Assert.Equal(1, after.Single(r => !r.Hidden).Index);
    }

    [Fact]
    public void L6_second_reconcile_pass_changes_nothing()
    {
        var after = Apply(Routes(5), Free);
        Assert.Empty(PlanRowChoice.Changed(after, PlanRowChoice.ChooseIndexed(after, Free.Routes, Free.PaymentEnabled)));
    }

    [Fact]
    public void L6_expire_with_one_route_hides_nothing()
    {
        Assert.All(Apply(Routes(1), Free), r => Assert.False(r.Hidden));
    }

    [Fact]
    public void L9_resubscribe_restores_every_route_in_the_original_order()
    {
        var start = Routes(5);
        var expired = Apply(start, Free);
        var back = Apply(expired, Plus);

        Assert.All(back, r => Assert.False(r.Hidden));
        Assert.Equal(start.Select(r => r.Id).ToList(), VisibleInOrder(back));
        Assert.Equal([1, 2, 3, 4, 5], back.OrderBy(r => r.Index).Select(r => r.Index).ToList());
        Assert.Equal(5, back.Select(r => r.Id).Distinct().Count());
    }

    [Fact]
    public void L9_resubscribe_never_loses_a_route_added_while_free()
    {
        var expired = Apply(Routes(5), Free);
        var withNew = expired.Append(new PlanRow(Guid.NewGuid(), false, 2)).ToList();
        var back = Apply(withNew, Plus);

        Assert.Equal(6, back.Count);
        Assert.Equal(5, back.Count(r => !r.Hidden));
        Assert.Equal(1, back.Count(r => r.Hidden));
        Assert.Equal(6, back.Select(r => r.Id).Distinct().Count());
    }

    [Fact]
    public void L12_plan_switch_keeps_every_active_route_active()
    {
        var rows = Apply(Routes(4), Plus);
        var other = Be3Kit.Compute(Taxi, true, Be3Kit.PlusId(Taxi));
        Assert.Equal(Plus.Routes, other.Routes);
        Assert.All(Apply(rows, other), r => Assert.False(r.Hidden));
    }

    [Fact]
    public void L14_payment_off_never_hides_a_route()
    {
        Assert.False(Off.PaymentEnabled);
        Assert.False(Off.CanUpgrade);
        Assert.All(Apply(Routes(5), Off), r => Assert.False(r.Hidden));
        Assert.All(Apply(Routes(8), Off), r => Assert.False(r.Hidden));
    }

    [Fact]
    public void L14_payment_off_after_on_restores_hidden_routes_in_order()
    {
        var start = Routes(5);
        var on = Apply(start, Free);
        Assert.Equal(4, on.Count(r => r.Hidden));
        var off = Apply(on, Off);
        Assert.All(off, r => Assert.False(r.Hidden));
        Assert.Equal(start.Select(r => r.Id).ToList(), VisibleInOrder(off));
    }

    [Fact]
    public void L15_deleting_a_hidden_route_before_a_rebuy_leaves_contiguous_indexes()
    {
        var expired = Apply(Routes(5), Free);
        var gone = expired.First(r => r.Hidden);
        var rest = expired.Where(r => r.Id != gone.Id).ToList();
        var back = Apply(rest, Plus);

        Assert.Equal(4, back.Count);
        Assert.All(back, r => Assert.False(r.Hidden));
        Assert.Equal([1, 2, 3, 4], back.OrderBy(r => r.Index).Select(r => r.Index).ToList());
    }

    [Fact]
    public void L15_two_saves_at_the_limit_only_one_passes_and_a_unique_clash_is_a_conflict()
    {
        Assert.True(PlanRowChoice.SaveAllowed(1, Free.Routes, 0));
        Assert.False(PlanRowChoice.SaveAllowed(2, Free.Routes, 1));
        Assert.Contains("UniqueViolation", Src("RentNearBy.Api", "Handlers", "TaxiHandlers.cs"));
    }

    [Fact]
    public void L15_owner_choice_after_expiry_keeps_exactly_the_free_limit()
    {
        var expired = Apply(Routes(5), Free);
        var ids = expired.Select(r => r.Id).ToList();
        var chosen = expired.First(r => r.Hidden).Id;

        Assert.True(PlanRowChoice.ChoiceNeeded(expired, Free.Routes));
        Assert.True(PlanRowChoice.ValidOwnerChoice(ids, [chosen], Free.Routes));
        Assert.False(PlanRowChoice.ValidOwnerChoice(ids, [chosen, expired[0].Id], Free.Routes));
        Assert.False(PlanRowChoice.ValidOwnerChoice(ids, [], Free.Routes));

        var swapped = Next(expired, PlanRowChoice.OwnerIndexed(expired, new HashSet<Guid> { chosen }));
        Assert.Equal([chosen], VisibleInOrder(swapped));
        Assert.Empty(PlanRowChoice.Changed(swapped, PlanRowChoice.ChooseIndexed(swapped, Free.Routes, Free.PaymentEnabled)));
    }

    [Fact]
    public void L16_hidden_routes_stay_out_of_public_reads_and_the_ways_network()
    {
        Assert.Contains("NOT r.\"HiddenByPlan\"", Src("RentNearBy.Infrastructure", "Repositories", "TaxiListingRepository.cs"));
        Assert.Contains("NOT r.\"HiddenByPlan\"", Src("RentNearBy.Infrastructure", "Repositories", "TaxiWaysNetworkRepository.cs"));
    }

    [Fact]
    public void L16_hidden_routes_never_ring_count_or_list_a_pickup()
    {
        Assert.Contains("NOT rt.\"HiddenByPlan\"", RentNearBy.Infrastructure.Services.TaxiRequests.TaxiCandidateQuery.RouteRule);
        Assert.Contains("NOT prt.\"HiddenByPlan\"", RentNearBy.Infrastructure.Services.TaxiRequests.TaxiCandidateQuery.CityPoolRule);
        Assert.Contains("NOT rt.\"HiddenByPlan\"", RentNearBy.Infrastructure.Services.TaxiRequests.Matching.MatcherSql.CitiesOfDrivers);
        Assert.Contains("NOT rt.\"HiddenByPlan\"", RentNearBy.Infrastructure.Services.TaxiRequests.TaxiPickupPlaces.FromCitiesSql);
        Assert.Contains("NOT rt.\"HiddenByPlan\"", RentNearBy.Infrastructure.Services.TaxiRequests.TaxiPickupPlaces.DriversToCitySql);
        var saved = Src("RentNearBy.Infrastructure", "Repositories", "SavedJourneyRepository.cs");
        Assert.Contains("WHERE NOT r.\"HiddenByPlan\"", saved);
        Assert.Contains("AND NOT r.\"HiddenByPlan\"", saved);
    }
}
