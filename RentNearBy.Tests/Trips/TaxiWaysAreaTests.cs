using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Services;

namespace RentNearBy.Tests.Trips;

public class TaxiWaysAreaTests
{
    private static readonly (double Lat, double Lng) Haldwani = (29.2183, 79.5130);
    private static readonly (double Lat, double Lng) Kathgodam = TaxiWaysFixture.Kathgodam;
    private static readonly (double Lat, double Lng) Nainital = (29.3919, 79.4542);
    private static readonly (double Lat, double Lng) Almora = (29.5971, 79.6591);
    private static readonly (double Lat, double Lng) Katghariya = (29.2250, 79.5700);
    private static readonly (double Lat, double Lng) Delhi = TaxiWaysFixture.Delhi;

    private static TaxiWaysSourceRow Row(string fn, (double, double) f, string tn, (double, double) t, int fare, Guid? taxi = null) =>
        TaxiWaysFixture.Row(fn, (f.Item1, f.Item2), tn, (t.Item1, t.Item2), fare, taxi: taxi);

    private static TaxiWaysNetworkSnapshot Hills(out Guid d1, out Guid d2, out Guid d3)
    {
        var s = TaxiWaysFixture.Build(
            Row("Haldwani", Haldwani, "Kathgodam", Kathgodam, 400),
            Row("Kathgodam", Kathgodam, "Nainital", Nainital, 900),
            Row("Haldwani", Haldwani, "Nainital", Nainital, 1500));
        d1 = Taxi(s, "Haldwani", "Kathgodam"); d2 = Taxi(s, "Kathgodam", "Nainital"); d3 = Taxi(s, "Haldwani", "Nainital");
        return s;
    }

    private static Guid Taxi(TaxiWaysNetworkSnapshot s, string a, string b) =>
        s.Links.Select(l => (l, From: s.Places[l.FromPlace].Name, To: s.Places[l.ToPlace].Name))
            .Single(x => (x.From == a && x.To == b) || (x.From == b && x.To == a)).l.TaxiId;

    [Fact]
    public void Distances_match_the_contract()
    {
        Assert.InRange(TaxiGeo.DistanceMeters(Kathgodam.Lat, Kathgodam.Lng, Nainital.Lat, Nainital.Lng), 15000, 17000);
        Assert.InRange(TaxiGeo.DistanceMeters(Haldwani.Lat, Haldwani.Lng, Kathgodam.Lat, Kathgodam.Lng), 4000, 7000);
        Assert.InRange(TaxiGeo.DistanceMeters(Nainital.Lat, Nainital.Lng, Almora.Lat, Almora.Lng), 28000, 34000);
    }

    [Fact]
    public void Virtual_start_off_every_route_gets_no_taxi_no_radius_sweep()
    {
        var s = Hills(out _, out _, out _);

        var r = TaxiWaysFixture.Search(s, Katghariya, Nainital);

        Assert.Empty(r.AllTheWay);
        Assert.Empty(r.Plans);
    }

    [Fact]
    public void Resolved_start_sees_only_the_taxis_of_its_own_place()
    {
        var s = Hills(out _, out _, out var d3);

        var r = TaxiWaysFixture.Search(s, Haldwani, Nainital);

        Assert.Equal(new[] { d3 }, r.AllTheWay.Select(c => c.Driver.TaxiId));
    }

    [Fact]
    public void A_hop_to_the_next_town_is_a_chain_through_a_real_route_stop()
    {
        var s = TaxiWaysFixture.Build(
            Row("Haldwani", Haldwani, "Kathgodam", Kathgodam, 400),
            Row("Kathgodam", Kathgodam, "Nainital", Nainital, 900));
        var a = TaxiWaysSearchEngine.Resolve(s, Haldwani.Lat, Haldwani.Lng, false, TaxiWaysFixture.Options);
        var b = TaxiWaysSearchEngine.Resolve(s, Nainital.Lat, Nainital.Lng, false, TaxiWaysFixture.Options);

        var (plans, _) = TaxiWaysChainSearch.Plans(s, a, b, 4, TaxiWaysFixture.Options);

        Assert.Equal(2, Assert.Single(plans).Cars);
    }

    [Fact]
    public void Mallital_start_with_a_driver_to_almora_sees_him_direct_not_a_hop_first()
    {
        var mallital = (29.3950, 79.4600);
        var s = TaxiWaysFixture.Build(
            Row("Nainital", Nainital, "Almora", Almora, 2200),
            Row("Mallital", mallital, "Nainital", Nainital, 150));

        var r = TaxiWaysFixture.Search(s, mallital, Almora);

        Assert.Single(r.AllTheWay);
        Assert.Empty(r.Plans);
    }

    [Fact]
    public void A_first_hop_outside_the_area_is_kept()
    {
        var s = TaxiWaysFixture.Build(
            Row("Delhi", Delhi, "Haldwani", Haldwani, 6000),
            Row("Haldwani", Haldwani, "Nainital", Nainital, 1500));

        var r = TaxiWaysFixture.Search(s, Delhi, Nainital);

        Assert.Empty(r.AllTheWay);
        var plan = Assert.Single(r.Plans);
        Assert.Equal(2, plan.Cars);
    }

    [Fact]
    public void Delhi_to_Almora_still_needs_two_cars()
    {
        var s = TaxiWaysFixture.Build(
            Row("Delhi", Delhi, "Haldwani", Haldwani, 6000),
            Row("Haldwani", Haldwani, "Almora", Almora, 3500));

        var r = TaxiWaysFixture.Search(s, Delhi, Almora);

        Assert.Empty(r.AllTheWay);
        Assert.Equal(2, Assert.Single(r.Plans).Cars);
    }

    [Fact]
    public void LegDrivers_candidates_match_the_direct_list_and_a_taxi_appears_once()
    {
        var s = Hills(out _, out _, out _);
        var o = TaxiWaysFixture.Options;
        var a = TaxiWaysSearchEngine.Resolve(s, Katghariya.Lat, Katghariya.Lng, false, o);
        var b = TaxiWaysSearchEngine.Resolve(s, Nainital.Lat, Nainital.Lng, false, o);

        var direct = TaxiWaysSearchEngine.Search(s, a, b, 4, o).AllTheWay.Select(c => c.Driver.TaxiId);
        var leg = TaxiWaysSearchEngine.Candidates(s, a, b, 4, o).Select(c => c.Driver.TaxiId).ToList();

        Assert.Equal(direct, leg);
        Assert.Equal(leg.Count, leg.Distinct().Count());
    }

    [Fact]
    public void A_single_driver_stays_a_single_candidate()
    {
        var s = Hills(out var d1, out _, out _);
        var o = TaxiWaysFixture.Options;
        var a = TaxiWaysSearchEngine.Resolve(s, Haldwani.Lat, Haldwani.Lng, false, o);
        var b = TaxiWaysSearchEngine.Resolve(s, Nainital.Lat, Nainital.Lng, false, o);

        Assert.Single(TaxiWaysSearchEngine.Candidates(s, a, b, 4, o), c => true);
    }

    [Fact]
    public void Same_taxi_on_two_routes_appears_once()
    {
        var taxi = Guid.NewGuid();
        var s = TaxiWaysFixture.Build(
            Row("Haldwani", Haldwani, "Nainital", Nainital, 1500, taxi),
            Row("Kathgodam", Kathgodam, "Nainital", Nainital, 900, taxi));

        var r = TaxiWaysFixture.Search(s, Haldwani, Nainital);

        Assert.Single(r.AllTheWay);
    }

    private static readonly (double Lat, double Lng) Mukteshwar = (29.4720, 79.6470);
    private static readonly (double Lat, double Lng) Village8KmWest = (29.2183, 79.4380);

    private static double Km((double Lat, double Lng) a, (double Lat, double Lng) b) =>
        TaxiGeo.DistanceMeters(a.Lat, a.Lng, b.Lat, b.Lng) / 1000;

    [Fact]
    public void Haldwani_to_Almora_does_not_see_the_Kathgodam_driver_direct_only_as_a_chain_stop()
    {
        var s = TaxiWaysFixture.Build(
            Row("Haldwani", Haldwani, "Kathgodam", Kathgodam, 400),
            Row("Kathgodam", Kathgodam, "Almora", Almora, 2500),
            Row("Haldwani", Haldwani, "Nainital", Nainital, 1500),
            Row("Nainital", Nainital, "Almora", Almora, 2200));
        var kathgodamAlmora = Taxi(s, "Kathgodam", "Almora");

        var r = TaxiWaysFixture.Search(s, Haldwani, Almora);

        Assert.Empty(r.AllTheWay);
        Assert.Equal(2900, r.Plans[0].Total);
        Assert.Equal(kathgodamAlmora, r.Plans[0].Legs[1].Top.Driver.TaxiId);
    }
    [Fact]
    public void Haldwani_to_Almora_with_only_a_Kathgodam_driver_is_a_two_car_chain_not_a_direct_car()
    {
        var s = TaxiWaysFixture.Build(
            Row("Haldwani", Haldwani, "Kathgodam", Kathgodam, 400),
            Row("Kathgodam", Kathgodam, "Almora", Almora, 2500));

        var r = TaxiWaysFixture.Search(s, Haldwani, Almora);

        Assert.Equal(2, Assert.Single(r.Plans).Cars);
        Assert.Empty(r.AllTheWay);
    }
    [Fact]
    public void Nainital_to_Haldwani_is_not_served_direct_by_a_car_that_ends_at_Kathgodam()
    {
        var s = TaxiWaysFixture.Build(
            Row("Nainital", Nainital, "Kathgodam", Kathgodam, 900),
            Row("Kathgodam", Kathgodam, "Haldwani", Haldwani, 400));

        var r = TaxiWaysFixture.Search(s, Nainital, Haldwani);

        Assert.Empty(r.AllTheWay);
        Assert.Equal(2, Assert.Single(r.Plans).Cars);
    }
    [Fact]
    public void Delhi_to_Almora_with_a_Kathgodam_Almora_driver_chains_three_cars_through_real_stops()
    {
        var s = TaxiWaysFixture.Build(
            Row("Delhi", Delhi, "Haldwani", Haldwani, 6000),
            Row("Haldwani", Haldwani, "Kathgodam", Kathgodam, 400),
            Row("Kathgodam", Kathgodam, "Almora", Almora, 2500));

        var r = TaxiWaysFixture.Search(s, Delhi, Almora);

        Assert.Empty(r.AllTheWay);
        Assert.Equal(3, Assert.Single(r.Plans).Cars);
    }
    [Fact]
    public void Delhi_to_Almora_two_long_cars_are_kept_and_every_hop_is_long()
    {
        var s = TaxiWaysFixture.Build(
            Row("Delhi", Delhi, "Haldwani", Haldwani, 6000),
            Row("Haldwani", Haldwani, "Almora", Almora, 3500));

        var r = TaxiWaysFixture.Search(s, Delhi, Almora);

        var plan = Assert.Single(r.Plans);
        Assert.Equal(2, plan.Cars);
        foreach (var leg in plan.Legs)
            Assert.True(TaxiGeo.DistanceMeters(leg.From.Lat, leg.From.Lng, leg.To.Lat, leg.To.Lng) > TaxiWaysFixture.Options.SamePlaceMeters);
    }

    [Fact]
    public void Village_8_km_from_Haldwani_to_Mukteshwar_sees_nobody_without_route_geometry()
    {
        var s = TaxiWaysFixture.Build(Row("Haldwani", Haldwani, "Mukteshwar", Mukteshwar, 2500));
        Assert.InRange(Km(Village8KmWest, Haldwani), 7, 9);

        var r = TaxiWaysFixture.Search(s, Village8KmWest, Mukteshwar);

        Assert.Empty(r.AllTheWay);
        Assert.Empty(r.Plans);
    }
    [Fact]
    public void Village_8_km_from_Haldwani_to_Almora_gets_no_chain_without_route_geometry()
    {
        var s = TaxiWaysFixture.Build(
            Row("Haldwani", Haldwani, "Nainital", Nainital, 1500),
            Row("Nainital", Nainital, "Almora", Almora, 2200));

        var r = TaxiWaysFixture.Search(s, Village8KmWest, Almora);

        Assert.Empty(r.AllTheWay);
        Assert.Empty(r.Plans);
    }
    [Fact]
    public void A_direct_all_the_way_taxi_beats_a_chain_that_costs_more()
    {
        var s = TaxiWaysFixture.Build(
            Row("Delhi", Delhi, "Almora", Almora, 8000),
            Row("Delhi", Delhi, "Haldwani", Haldwani, 6000),
            Row("Haldwani", Haldwani, "Almora", Almora, 3500));

        var r = TaxiWaysFixture.Search(s, Delhi, Almora);

        Assert.Single(r.AllTheWay);
        Assert.Empty(r.Plans);
    }

    [Fact]
    public void A_chain_cheaper_than_the_direct_taxi_is_kept()
    {
        var s = TaxiWaysFixture.Build(
            Row("Delhi", Delhi, "Almora", Almora, 12000),
            Row("Delhi", Delhi, "Haldwani", Haldwani, 4000),
            Row("Haldwani", Haldwani, "Almora", Almora, 3500));

        var r = TaxiWaysFixture.Search(s, Delhi, Almora);

        Assert.Single(r.AllTheWay);
        Assert.Equal(2, Assert.Single(r.Plans).Cars);
    }

    private sealed class FakeNetwork(TaxiWaysNetworkSnapshot snapshot) : ITaxiWaysNetwork
    {
        public Task<TaxiWaysNetworkSnapshot?> GetAsync(CancellationToken ct = default) => Task.FromResult<TaxiWaysNetworkSnapshot?>(snapshot);
        public string? CurrentVersion => snapshot.Version;
    }

    private sealed class FakeSource(string? state) : ITaxiWaysNetworkSource
    {
        public Task<IReadOnlyList<TaxiWaysSourceRow>> LoadAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TaxiWaysSourceRow>>([]);
    }

    private static TaxiWaysSearch Searcher(TaxiWaysNetworkSnapshot s, string? state = "Uttarakhand")
    {
        var services = new ServiceCollection().AddSingleton<ITaxiWaysNetworkSource>(new FakeSource(state)).BuildServiceProvider();
        return new TaxiWaysSearch(
            new FakeNetwork(s), services.GetRequiredService<IServiceScopeFactory>(), services,
            new MemoryCache(new MemoryCacheOptions()), Options.Create(new TaxiWaysOptions()), null!,
            NullLogger<TaxiWaysSearch>.Instance);
    }

    [Fact]
    public async Task Change_driver_on_leg_two_lists_exactly_the_plan_leg_drivers()
    {
        var s = TaxiWaysFixture.Build(
            Row("Delhi", Delhi, "Haldwani", Haldwani, 6000),
            Row("Haldwani", Haldwani, "Almora", Almora, 3500),
            Row("Kathgodam", Kathgodam, "Almora", Almora, 2500));
        var r = TaxiWaysFixture.Search(s, Delhi, Almora);
        var plan = Assert.Single(r.Plans);
        var leg2 = plan.Legs[1];

        var fares = await Searcher(s).LegFaresAsync(leg2.LegKey, s.Drivers.Keys.ToList(), plan.Legs[0].Top.Link.RouteId);

        Assert.Equal(leg2.Drivers.Select(c => c.Driver.TaxiId).Order(), fares.Keys.Order());
        Assert.DoesNotContain(fares.Keys, id => id == Taxi(s, "Kathgodam", "Almora"));
    }

    [Fact]
    public async Task Change_driver_on_a_single_car_first_leg_lists_only_the_place_drivers()
    {
        var s = Hills(out _, out _, out var d3);
        var r = TaxiWaysFixture.Search(s, Haldwani, Nainital);
        var key = TaxiWaysKeys.LegKey(r.From.Key, r.To.Key);

        var fares = await Searcher(s).LegFaresAsync(key, s.Drivers.Keys.ToList());

        Assert.Equal(new[] { d3 }, fares.Keys);
    }
    [Fact]
    public async Task Reach_first_place_is_given_in_a_launch_state_too_when_no_way_exists()
    {
        var s = TaxiWaysFixture.Build(Row("Nainital", Nainital, "Almora", Almora, 2200));

        var result = await Searcher(s, "Uttarakhand").SearchAsync(new TaxiWaysQuery(
            Village8KmWest.Lat, Village8KmWest.Lng, Almora.Lat, Almora.Lng, 4));

        Assert.NotNull(result);
        Assert.Empty(result!.AllTheWay);
        Assert.Empty(result.Plans);
        Assert.Equal("Nainital", result.ReachFirstPlace?.Name);
    }

    [Fact]
    public void Trip_searches_list_the_drivers_based_here_first()
    {
        var near = Guid.NewGuid();
        var far = Guid.NewGuid();
        var rowNear = Row("Haldwani", Haldwani, "Nainital", Nainital, 1500, near);
        var rowFar = Row("Kathgodam", Kathgodam, "Nainital", Nainital, 900, far);
        rowNear.BaseLat = Haldwani.Lat; rowNear.BaseLng = Haldwani.Lng;
        rowFar.BaseLat = Delhi.Lat; rowFar.BaseLng = Delhi.Lng;
        var s = TaxiWaysFixture.Build(rowNear, rowFar);

        var r = TaxiWaysFixture.Search(s, Haldwani, Nainital);

        Assert.Equal(near, r.AllTheWay[0].Driver.TaxiId);
        Assert.True(r.AllTheWay[0].BasedHere);
    }
}
