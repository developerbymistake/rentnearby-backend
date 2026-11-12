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
    public void Virtual_start_in_area_sees_D3_and_D2_as_direct_cars_and_no_plan()
    {
        var s = Hills(out var d1, out var d2, out var d3);

        var r = TaxiWaysFixture.Search(s, Katghariya, Nainital);

        Assert.Equal(new[] { d2, d3 }.Order(), r.AllTheWay.Select(c => c.Driver.TaxiId).Order());
        Assert.DoesNotContain(r.AllTheWay, c => c.Driver.TaxiId == d1);
        Assert.Empty(r.Plans);
        var d2Row = r.AllTheWay.Single(c => c.Driver.TaxiId == d2);
        Assert.Equal(s.Places.Values.Single(p => p.Name == "Kathgodam").Key, d2Row.Link.MeetAtPlace);
    }

    [Fact]
    public void Resolved_start_gets_the_same_door_to_door_list()
    {
        var s = Hills(out var d1, out var d2, out var d3);

        var r = TaxiWaysFixture.Search(s, Haldwani, Nainital);

        Assert.Equal(new[] { d2, d3 }.Order(), r.AllTheWay.Select(c => c.Driver.TaxiId).Order());
        Assert.Empty(r.Plans);
    }

    [Fact]
    public void An_in_area_first_hop_is_never_a_chain()
    {
        var s = TaxiWaysFixture.Build(
            Row("Haldwani", Haldwani, "Kathgodam", Kathgodam, 400),
            Row("Kathgodam", Kathgodam, "Nainital", Nainital, 900));
        var a = TaxiWaysSearchEngine.Resolve(s, Haldwani.Lat, Haldwani.Lng, false, TaxiWaysFixture.Options);
        var b = TaxiWaysSearchEngine.Resolve(s, Nainital.Lat, Nainital.Lng, false, TaxiWaysFixture.Options);

        var (plans, _) = TaxiWaysChainSearch.Plans(s, a, b, 4, TaxiWaysFixture.Options);

        Assert.Empty(plans);
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
        var leg = TaxiWaysSearchEngine.Candidates(s, a, b, 4, o, area: true).Select(c => c.Driver.TaxiId).ToList();

        Assert.Equal(direct, leg);
        Assert.Equal(leg.Count, leg.Distinct().Count());
    }

    [Fact]
    public void Chain_legs_do_not_widen_to_the_area()
    {
        var s = Hills(out var d1, out _, out _);
        var o = TaxiWaysFixture.Options;
        var a = TaxiWaysSearchEngine.Resolve(s, Haldwani.Lat, Haldwani.Lng, false, o);
        var b = TaxiWaysSearchEngine.Resolve(s, Nainital.Lat, Nainital.Lng, false, o);

        Assert.Single(TaxiWaysSearchEngine.Candidates(s, a, b, 4, o), c => true);
        Assert.Equal(2, TaxiWaysSearchEngine.Candidates(s, a, b, 4, o, area: true).Count);
    }

    [Fact]
    public void Same_taxi_on_two_start_places_in_area_appears_once()
    {
        var taxi = Guid.NewGuid();
        var s = TaxiWaysFixture.Build(
            Row("Haldwani", Haldwani, "Nainital", Nainital, 1500, taxi),
            Row("Kathgodam", Kathgodam, "Nainital", Nainital, 900, taxi));

        var r = TaxiWaysFixture.Search(s, Katghariya, Nainital);

        Assert.Single(r.AllTheWay);
    }

    private static readonly (double Lat, double Lng) Mukteshwar = (29.4720, 79.6470);
    private static readonly (double Lat, double Lng) Village8KmWest = (29.2183, 79.4380);

    private static double Km((double Lat, double Lng) a, (double Lat, double Lng) b) =>
        TaxiGeo.DistanceMeters(a.Lat, a.Lng, b.Lat, b.Lng) / 1000;

    [Fact]
    public void Dev_at_Haldwani_to_Almora_gets_the_area_driver_direct_and_no_dearer_chain()
    {
        var s = TaxiWaysFixture.Build(
            Row("Haldwani", Haldwani, "Kathgodam", Kathgodam, 400),
            Row("Kathgodam", Kathgodam, "Almora", Almora, 2500),
            Row("Haldwani", Haldwani, "Nainital", Nainital, 1500),
            Row("Nainital", Nainital, "Almora", Almora, 2200));
        var kathgodamAlmora = Taxi(s, "Kathgodam", "Almora");

        var r = TaxiWaysFixture.Search(s, Haldwani, Almora);

        Assert.Contains(r.AllTheWay, c => c.Driver.TaxiId == kathgodamAlmora);
        Assert.Empty(r.Plans);
    }

    [Fact]
    public void Dev_at_Haldwani_never_gets_a_chain_that_starts_with_the_local_Kathgodam_hop()
    {
        var s = TaxiWaysFixture.Build(
            Row("Haldwani", Haldwani, "Kathgodam", Kathgodam, 400),
            Row("Kathgodam", Kathgodam, "Almora", Almora, 2500));

        var r = TaxiWaysFixture.Search(s, Haldwani, Almora);

        Assert.Empty(r.Plans);
        Assert.Single(r.AllTheWay);
    }

    [Fact]
    public void Meera_at_Nainital_to_Haldwani_sees_the_car_that_ends_in_the_area_not_a_short_last_hop()
    {
        var s = TaxiWaysFixture.Build(
            Row("Nainital", Nainital, "Kathgodam", Kathgodam, 900),
            Row("Kathgodam", Kathgodam, "Haldwani", Haldwani, 400));
        var nainitalKathgodam = Taxi(s, "Nainital", "Kathgodam");

        var r = TaxiWaysFixture.Search(s, Nainital, Haldwani);

        Assert.Contains(r.AllTheWay, c => c.Driver.TaxiId == nainitalKathgodam);
        Assert.Empty(r.Plans);
    }

    [Fact]
    public void Delhi_to_Almora_with_a_Kathgodam_Almora_driver_gets_no_plan_with_a_short_hop()
    {
        var s = TaxiWaysFixture.Build(
            Row("Delhi", Delhi, "Haldwani", Haldwani, 6000),
            Row("Haldwani", Haldwani, "Kathgodam", Kathgodam, 400),
            Row("Kathgodam", Kathgodam, "Almora", Almora, 2500));

        var r = TaxiWaysFixture.Search(s, Delhi, Almora);

        Assert.Empty(r.AllTheWay);
        Assert.Empty(r.Plans);
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
            Assert.True(TaxiGeo.DistanceMeters(leg.From.Lat, leg.From.Lng, leg.To.Lat, leg.To.Lng) > TaxiWaysFixture.Options.MinLegMeters);
    }

    [Fact]
    public void Village_8_km_from_Haldwani_to_Mukteshwar_sees_the_Haldwani_driver_direct()
    {
        var s = TaxiWaysFixture.Build(Row("Haldwani", Haldwani, "Mukteshwar", Mukteshwar, 2500));
        Assert.InRange(Km(Village8KmWest, Haldwani), 7, 9);

        var r = TaxiWaysFixture.Search(s, Village8KmWest, Mukteshwar);

        var car = Assert.Single(r.AllTheWay);
        Assert.Equal(s.Places.Values.Single(p => p.Name == "Haldwani").Key, car.Link.MeetAtPlace);
        Assert.Empty(r.Plans);
    }

    [Fact]
    public void Village_8_km_from_Haldwani_to_Almora_gets_a_chain_seeded_from_the_area()
    {
        var s = TaxiWaysFixture.Build(
            Row("Haldwani", Haldwani, "Nainital", Nainital, 1500),
            Row("Nainital", Nainital, "Almora", Almora, 2200));

        var r = TaxiWaysFixture.Search(s, Village8KmWest, Almora);

        Assert.Empty(r.AllTheWay);
        var plan = Assert.Single(r.Plans);
        Assert.Equal(2, plan.Cars);
        Assert.Equal(Taxi(s, "Haldwani", "Nainital"), plan.Legs[0].Top.Driver.TaxiId);
        Assert.Equal(Taxi(s, "Nainital", "Almora"), plan.Legs[1].Top.Driver.TaxiId);
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
        public Task<(Guid? DistrictId, string? StateName)> FindDistrictAsync(double lat, double lng, CancellationToken ct = default) =>
            Task.FromResult<(Guid? DistrictId, string? StateName)>((null, state));
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
    public async Task Change_driver_on_the_last_leg_keeps_the_destination_area_drivers_of_the_plan()
    {
        var kasarDevi = (29.6430, 79.6690);
        var s = TaxiWaysFixture.Build(
            Row("Delhi", Delhi, "Haldwani", Haldwani, 6000),
            Row("Haldwani", Haldwani, "Almora", Almora, 3500),
            Row("Haldwani", Haldwani, "Kasar Devi", kasarDevi, 3600));
        var r = TaxiWaysFixture.Search(s, Delhi, Almora);
        var plan = Assert.Single(r.Plans);
        var last = plan.Legs[^1];
        var kasarTaxi = Taxi(s, "Haldwani", "Kasar Devi");
        Assert.Contains(last.Drivers, c => c.Driver.TaxiId == kasarTaxi);
        var previousRoute = plan.Legs[0].Top.Link.RouteId;

        var searcher = Searcher(s);
        var forLastLeg = await searcher.LegFaresAsync(last.LegKey, s.Drivers.Keys.ToList(), previousRoute, default, area: false, endArea: true);
        var guessed = await searcher.LegFaresAsync(last.LegKey, s.Drivers.Keys.ToList(), previousRoute);

        Assert.Equal(last.Drivers.Select(c => c.Driver.TaxiId).Order(), forLastLeg.Keys.Order());
        Assert.DoesNotContain(kasarTaxi, guessed.Keys);
    }

    [Fact]
    public async Task Change_driver_on_a_single_car_first_leg_keeps_the_area_drivers()
    {
        var s = Hills(out _, out var d2, out var d3);
        var r = TaxiWaysFixture.Search(s, Haldwani, Nainital);
        var key = TaxiWaysKeys.LegKey(r.From.Key, r.To.Key);

        var fares = await Searcher(s).LegFaresAsync(key, s.Drivers.Keys.ToList());

        Assert.Equal(new[] { d2, d3 }.Order(), fares.Keys.Order());
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
