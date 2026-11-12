using RentNearBy.Infrastructure.Services;
using RentNearBy.Tests.Trips;

namespace RentNearBy.Tests.Journeys;

// Replay of the taxi-area simulator (UI_Template_resume/journeys/taxi-area.md) on the real engine, with its own names and numbers.
// Network: D1 Haldwani-Kathgodam 400, D2 Kathgodam-Nainital 900, D3 Haldwani-Nainital 1500, D4 Delhi-Haldwani 6000, D5 Haldwani-Almora 3500.
// Expected values are literals from the owner's rule (a traveller sees only the taxis of his own place or route, no km sweep).
public class J1TaxiAreaJourneyTests
{
    private static readonly (double Lat, double Lng) Haldwani = (29.2183, 79.5130);
    private static readonly (double Lat, double Lng) Kathgodam = (29.2656, 79.5262);
    private static readonly (double Lat, double Lng) Nainital = (29.3919, 79.4542);
    private static readonly (double Lat, double Lng) Almora = (29.5971, 79.6591);
    private static readonly (double Lat, double Lng) Delhi = (28.6139, 77.2090);
    private static readonly (double Lat, double Lng) Katghariya = (29.2250, 79.5700);
    private static readonly (double Lat, double Lng) Kusumkhera = (29.2000, 79.5000);
    private static readonly (double Lat, double Lng) Mallital = (29.3950, 79.4600);
    private static readonly (double Lat, double Lng) Bhimtal = (29.3436, 79.5535);
    private static readonly (double Lat, double Lng) Karan = (29.5800, 79.2500);

    private static TaxiWaysSourceRow Row((string, (double, double)) a, (string, (double, double)) b, int fare, Guid? taxi = null) =>
        TaxiWaysFixture.Row(a.Item1, a.Item2, b.Item1, b.Item2, fare, taxi: taxi);

    private static (string, (double, double)) P(string n, (double Lat, double Lng) c) => (n, (c.Lat, c.Lng));

    private sealed class Net
    {
        public required TaxiWaysNetworkSnapshot S { get; init; }
        public Guid D1, D2, D3, D4, D5;
    }

    private static Net Build(params TaxiWaysSourceRow[] extra)
    {
        var d = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();
        var rows = new List<TaxiWaysSourceRow>
        {
            Row(P("Haldwani", Haldwani), P("Kathgodam", Kathgodam), 400, d[0]),
            Row(P("Kathgodam", Kathgodam), P("Nainital", Nainital), 900, d[1]),
            Row(P("Haldwani", Haldwani), P("Nainital", Nainital), 1500, d[2]),
            Row(P("Delhi", Delhi), P("Haldwani", Haldwani), 6000, d[3]),
            Row(P("Haldwani", Haldwani), P("Almora", Almora), 3500, d[4]),
        };
        rows.AddRange(extra);
        return new Net { S = TaxiWaysFixture.Build(rows.ToArray()), D1 = d[0], D2 = d[1], D3 = d[2], D4 = d[3], D5 = d[4] };
    }

    private static TaxiWaysSearchResult Search(Net n, (double Lat, double Lng) from, (double Lat, double Lng) to, bool myLocation = false)
    {
        var o = TaxiWaysFixture.Options;
        var a = TaxiWaysSearchEngine.Resolve(n.S, from.Lat, from.Lng, myLocation, o);
        var b = TaxiWaysSearchEngine.Resolve(n.S, to.Lat, to.Lng, false, o);
        return TaxiWaysSearchEngine.Search(n.S, a, b, 4, o);
    }

    private static HashSet<Guid> Ids(TaxiWaysSearchResult r) => r.AllTheWay.Select(c => c.Driver.TaxiId).ToHashSet();

    private static double Km(TaxiWaysStop a, TaxiWaysStop b) => TaxiGeo.DistanceMeters(a.Lat, a.Lng, b.Lat, b.Lng) / 1000;

    [Fact]
    public void Dev_in_Kusumkhera_to_Nainital_sees_only_D3_the_Haldwani_taxi()
    {
        var n = Build();
        var r = Search(n, Kusumkhera, Nainital);
        Assert.Equal(new HashSet<Guid> { n.D3 }, Ids(r));
        Assert.DoesNotContain(r.Plans, p => p.Legs.Any(l => l.Drivers.Any(c => c.Driver.TaxiId == n.D3)));
    }
    [Fact]
    public void Dev_to_Almora_sees_only_D5()
    {
        var n = Build();
        var r = Search(n, Kusumkhera, Almora);
        Assert.Equal(new HashSet<Guid> { n.D5 }, Ids(r));
        Assert.Empty(r.Plans);
    }

    [Fact]
    public void A_traveller_in_Haldwani_gets_the_Haldwani_cars_to_Nainital_Almora_and_Delhi()
    {
        var n = Build();
        Assert.Equal(new HashSet<Guid> { n.D3 }, Ids(Search(n, Kusumkhera, Nainital)));
        Assert.Equal(new HashSet<Guid> { n.D5 }, Ids(Search(n, Kusumkhera, Almora)));
        Assert.Equal(new HashSet<Guid> { n.D4 }, Ids(Search(n, Kusumkhera, Delhi)));
        Assert.Empty(Search(n, Kusumkhera, Delhi).Plans);
    }

    [Fact]
    public void Asha_in_Katghariya_outside_every_place_and_route_sees_nobody()
    {
        var n = Build();
        Assert.Empty(Ids(Search(n, Katghariya, Nainital)));
        Assert.Empty(Ids(Search(n, Katghariya, Almora, myLocation: true)));
    }
    [Fact]
    public void My_location_in_Haldwani_to_Almora_gets_D5_and_no_three_car_plan_dearer_than_it()
    {
        var n = Build();
        var r = Search(n, Kusumkhera, Almora, myLocation: true);
        Assert.Contains(n.D5, Ids(r));
        Assert.DoesNotContain(r.Plans, p => p.Cars >= 3);
        Assert.DoesNotContain(r.Plans, p => p.Total >= 3500);
    }
    [Fact]
    public void Dev_to_Almora_with_a_Kathgodam_Almora_driver_sees_only_D5_direct_and_the_Kathgodam_one_in_a_chain()
    {
        var n = Build(Row(P("Kathgodam", Kathgodam), P("Almora", Almora), 2500));
        var r = Search(n, Kusumkhera, Almora);
        Assert.Equal(new HashSet<Guid> { n.D5 }, Ids(r));
        Assert.DoesNotContain(r.Plans, p => p.Total >= 3500);
        Assert.DoesNotContain(r.Plans, p => p.Cars >= 3);
    }
    [Fact]
    public void Meera_in_Mallital_to_Haldwani_sees_D3_and_every_hop_of_a_plan_leaves_the_place()
    {
        var n = Build();
        var r = Search(n, Mallital, Haldwani);
        Assert.Equal(new HashSet<Guid> { n.D3 }, Ids(r));
        foreach (var plan in r.Plans)
            foreach (var leg in plan.Legs)
                Assert.True(Km(leg.From, leg.To) > 5, $"{leg.From.Name} to {leg.To.Name} stays in one place");
    }
    [Fact]
    public void Meera_to_Delhi_needs_two_cars_for_Rs_7500_and_no_hop_stays_in_one_place()
    {
        var n = Build();
        var r = Search(n, Mallital, Delhi);
        Assert.Empty(r.AllTheWay);
        var plan = r.Plans.Single(p => p.Cars == 2);
        Assert.Equal(7500, plan.Total);
        Assert.All(r.Plans.SelectMany(p => p.Legs), leg => Assert.True(Km(leg.From, leg.To) > 5));
    }
    [Fact]
    public void Delhi_to_Almora_stays_a_two_car_journey_for_Rs_9500()
    {
        var n = Build();
        var r = Search(n, Delhi, Almora);
        Assert.Empty(r.AllTheWay);
        var plan = Assert.Single(r.Plans);
        Assert.Equal(2, plan.Cars);
        Assert.Equal(9500, plan.Total);
    }

    [Fact]
    public void Driver_based_in_Bhimtal_is_seen_from_Bhimtal_and_not_from_far_outside_the_area()
    {
        var n = Build(Row(P("Bhimtal", Bhimtal), P("Nainital", Nainital), 600));
        var bhimtalTaxi = n.S.Drivers.Keys.Except([n.D1, n.D2, n.D3, n.D4, n.D5]).Single();
        Assert.Contains(bhimtalTaxi, Ids(Search(n, Bhimtal, Nainital)));
        Assert.DoesNotContain(bhimtalTaxi, Ids(Search(n, Delhi, Nainital)));
    }

    [Fact]
    public void Karan_far_from_every_taxi_place_gets_no_way_and_the_nearest_place_to_reach_first()
    {
        // 40 km or more from any place: nothing can be offered, but the traveller is told where taxis start (no dead end).
        var n = Build();
        var r = Search(n, Karan, Nainital);
        Assert.Empty(r.AllTheWay);
        Assert.Empty(r.Plans);
        Assert.True(Km(new TaxiWaysStop("k", "Karan", Karan.Lat, Karan.Lng), new TaxiWaysStop("h", "Haldwani", Haldwani.Lat, Haldwani.Lng)) > 15);
    }
}
