namespace RentNearBy.Tests.Trips;

// B1/M19: a driver who serves consecutive stretches of a plan is ONE car (fares added), never "change car" with the same driver.
public class TaxiWaysSameDriverTests
{
    private static readonly (double Lat, double Lng) Haldwani = (29.2183, 79.5130);
    private static readonly (double Lat, double Lng) Kathgodam = (29.2656, 79.5262);
    private static readonly (double Lat, double Lng) Almora = (29.5971, 79.6591);

    private static TaxiWaysAnchor Anchor(TaxiWaysNetworkSnapshot s, (double Lat, double Lng) p) =>
        TaxiWaysSearchEngine.Resolve(s, p.Lat, p.Lng, false, TaxiWaysFixture.Options);

    private static (TaxiWaysNetworkSnapshot S, Guid Pradeep) PradeepNet()
    {
        var pradeep = Guid.NewGuid();
        var s = TaxiWaysFixture.Build(
            TaxiWaysFixture.Row("Haldwani", Haldwani, "Kathgodam", Kathgodam, 250, taxi: pradeep),
            TaxiWaysFixture.Row("Kathgodam", Kathgodam, "Almora", Almora, 3000, taxi: pradeep));
        return (s, pradeep);
    }

    private static bool SameDriverBackToBack(TaxiWaysPlan p) =>
        p.Legs.Zip(p.Legs.Skip(1)).Any(x => x.First.Top.Driver.TaxiId == x.Second.Top.Driver.TaxiId);

    [Fact]
    public void BuildPlan_same_driver_on_consecutive_legs_is_one_car_with_added_fares()
    {
        var (s, pradeep) = PradeepNet();
        var plan = TaxiWaysSearchEngine.BuildPlan(
            s, [Anchor(s, Haldwani), Anchor(s, Kathgodam), Anchor(s, Almora)], 1, TaxiWaysFixture.Options);

        Assert.NotNull(plan);
        Assert.Equal(1, plan!.Cars);
        Assert.Equal(pradeep, plan.Legs[0].Top.Driver.TaxiId);
        Assert.Equal(3250, plan.Total);
        Assert.Equal(1, plan.Legs[0].Index);
    }

    [Fact]
    public void Merged_leg_is_found_again_from_its_own_leg_key()
    {
        var (s, pradeep) = PradeepNet();
        var plan = TaxiWaysSearchEngine.BuildPlan(
            s, [Anchor(s, Haldwani), Anchor(s, Kathgodam), Anchor(s, Almora)], 1, TaxiWaysFixture.Options)!;
        Assert.True(TaxiWaysKeys.TryParseLegKey(plan.Legs[0].LegKey, out var from, out var to));
        var a = TaxiWaysSearchEngine.AnchorFromKey(s, from, TaxiWaysFixture.Options)!;
        var b = TaxiWaysSearchEngine.AnchorFromKey(s, to, TaxiWaysFixture.Options, true)!;

        var again = TaxiWaysSearchEngine.Candidates(s, a, b, 1, TaxiWaysFixture.Options);

        Assert.Contains(again, c => c.Driver.TaxiId == pradeep && c.Link.EstimatedFare == 3250);
        Assert.True(TaxiWaysKeys.TryParsePlanKey(plan.PlanKey, out var keys));
        Assert.Equal(2, keys.Count);
    }

    [Fact]
    public void Search_offers_the_relay_driver_all_the_way_and_never_two_cars_of_one_driver()
    {
        var (s, pradeep) = PradeepNet();
        var r = TaxiWaysFixture.Search(s, Haldwani, Almora, 1);

        Assert.Contains(r.AllTheWay, c => c.Driver.TaxiId == pradeep && c.Link.EstimatedFare == 3250);
        Assert.DoesNotContain(r.Plans, SameDriverBackToBack);
        Assert.DoesNotContain(r.Plans, p => p.Cars == 2 && p.Legs.All(l => l.Top.Driver.TaxiId == pradeep));
    }

    [Fact]
    public void Chain_search_never_returns_a_plan_with_the_same_driver_back_to_back()
    {
        var (s, _) = PradeepNet();
        var (plans, _) = TaxiWaysChainSearch.Plans(
            s, Anchor(s, Haldwani), Anchor(s, Almora), 1, TaxiWaysFixture.Options);
        Assert.DoesNotContain(plans, SameDriverBackToBack);
    }

    [Fact]
    public void Two_different_drivers_still_give_two_cars()
    {
        var s = TaxiWaysFixture.Build(
            TaxiWaysFixture.Row("Haldwani", Haldwani, "Kathgodam", Kathgodam, 250),
            TaxiWaysFixture.Row("Kathgodam", Kathgodam, "Almora", Almora, 3000));
        var plan = TaxiWaysSearchEngine.BuildPlan(
            s, [Anchor(s, Haldwani), Anchor(s, Kathgodam), Anchor(s, Almora)], 1, TaxiWaysFixture.Options);

        Assert.NotNull(plan);
        Assert.Equal(2, plan!.Cars);
        Assert.Equal(3250, plan.Total);
        Assert.Empty(TaxiWaysFixture.Search(s, Haldwani, Almora, 1).AllTheWay);
    }
}
