namespace RentNearBy.Tests.Trips;

// Owner rule: a traveller anywhere in a city sees only the taxis whose route starts at THAT city (the same-place
// resolution, SamePlaceMeters), never those of a town within some km (Bhimtal, Ramnagar, Ramgarh for a Haldwani search).
public class TaxiWaysSameCityRuleTests
{
    private static readonly (double Lat, double Lng) Haldwani = (29.2183, 79.5130);
    private static readonly (double Lat, double Lng) HaldwaniEastEdge = (29.2183, 79.5560);
    private static readonly (double Lat, double Lng) HaldwaniOutside = (29.2183, 79.5760);
    private static readonly (double Lat, double Lng) Kathgodam = (29.2656, 79.5262);
    private static readonly (double Lat, double Lng) Nainital = (29.3919, 79.4542);
    private static readonly (double Lat, double Lng) Ranikhet = (29.6434, 79.4322);
    private static readonly (double Lat, double Lng) Bhimtal = (29.3436, 79.5535);
    private static readonly (double Lat, double Lng) Ramnagar = (29.3944, 79.1281);
    private static readonly (double Lat, double Lng) Ramgarh = (29.4167, 79.5667);
    private static readonly (double Lat, double Lng) Almora = (29.5971, 79.6591);
    private static readonly (double Lat, double Lng) Delhi = TaxiWaysFixture.Delhi;

    private sealed class Net
    {
        public required TaxiWaysNetworkSnapshot S { get; init; }
        public Guid HaldwaniNainital, HaldwaniRanikhet, BhimtalNainital, RamnagarRanikhet, RamgarhNainital, DelhiHaldwani, HaldwaniAlmora;
    }

    private static Net Build()
    {
        var d = Enumerable.Range(0, 7).Select(_ => Guid.NewGuid()).ToArray();
        var rows = new[]
        {
            TaxiWaysFixture.Row("Haldwani", Haldwani, "Nainital", Nainital, 1500, taxi: d[0]),
            TaxiWaysFixture.Row("Haldwani", Haldwani, "Ranikhet", Ranikhet, 3000, taxi: d[1]),
            TaxiWaysFixture.Row("Bhimtal", Bhimtal, "Nainital", Nainital, 600, taxi: d[2]),
            TaxiWaysFixture.Row("Ramnagar", Ramnagar, "Ranikhet", Ranikhet, 2800, taxi: d[3]),
            TaxiWaysFixture.Row("Ramgarh", Ramgarh, "Nainital", Nainital, 700, taxi: d[4]),
            TaxiWaysFixture.Row("Delhi", Delhi, "Haldwani", Haldwani, 6000, taxi: d[5]),
            TaxiWaysFixture.Row("Haldwani", Haldwani, "Almora", Almora, 3500, taxi: d[6]),
            TaxiWaysFixture.Row("Kathgodam", Kathgodam, "Almora", Almora, 2500),
        };
        return new Net
        {
            S = TaxiWaysFixture.Build(rows),
            HaldwaniNainital = d[0], HaldwaniRanikhet = d[1], BhimtalNainital = d[2], RamnagarRanikhet = d[3],
            RamgarhNainital = d[4], DelhiHaldwani = d[5], HaldwaniAlmora = d[6],
        };
    }

    private static HashSet<Guid> Ids(TaxiWaysSearchResult r) => r.AllTheWay.Select(c => c.Driver.TaxiId).ToHashSet();

    [Theory]
    [InlineData(29.2183, 79.5130)] // centre
    [InlineData(29.2183, 79.5560)] // far east corner, 4.2 km from the centre, 14 km from Bhimtal
    public void Anywhere_in_Haldwani_to_Nainital_sees_only_the_Haldwani_Nainital_taxi(double lat, double lng)
    {
        var n = Build();
        var r = TaxiWaysFixture.Search(n.S, (lat, lng), Nainital);
        Assert.Equal(new HashSet<Guid> { n.HaldwaniNainital }, Ids(r));
    }

    [Fact]
    public void Anywhere_in_Haldwani_to_Ranikhet_sees_only_the_Haldwani_Ranikhet_taxi()
    {
        var n = Build();
        Assert.Equal(new HashSet<Guid> { n.HaldwaniRanikhet }, Ids(TaxiWaysFixture.Search(n.S, Haldwani, Ranikhet)));
        Assert.Equal(new HashSet<Guid> { n.HaldwaniRanikhet }, Ids(TaxiWaysFixture.Search(n.S, HaldwaniEastEdge, Ranikhet)));
    }

    [Fact]
    public void Traveller_in_Bhimtal_sees_the_Bhimtal_taxi_only()
    {
        var n = Build();
        Assert.Equal(new HashSet<Guid> { n.BhimtalNainital }, Ids(TaxiWaysFixture.Search(n.S, Bhimtal, Nainital)));
    }

    [Fact]
    public void Traveller_in_Ramgarh_and_Ramnagar_see_their_own_taxis_only()
    {
        var n = Build();
        Assert.Equal(new HashSet<Guid> { n.RamgarhNainital }, Ids(TaxiWaysFixture.Search(n.S, Ramgarh, Nainital)));
        Assert.Equal(new HashSet<Guid> { n.RamnagarRanikhet }, Ids(TaxiWaysFixture.Search(n.S, Ramnagar, Ranikhet)));
    }

    private static readonly (double Lat, double Lng) Lalkuan = (29.0700, 79.5100);
    private static readonly (double Lat, double Lng) MidRoad = (29.1440, 79.5115);

    private static string Line(params (double Lat, double Lng)[] pts) =>
        "{\"type\":\"LineString\",\"coordinates\":[" + string.Join(",", pts.Select(p =>
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"[{p.Lng},{p.Lat}]"))) + "]}";

    // Lalkuan -> Haldwani -> Kathgodam -> Nainital: a route that starts outside Haldwani but passes through it.
    private static (TaxiWaysNetworkSnapshot S, Guid Through, Guid Bhim, Guid Own) Geo()
    {
        var through = TaxiWaysFixture.Row("Lalkuan", Lalkuan, "Nainital", Nainital, 1800);
        through.GeometryJson = Line(Lalkuan, Haldwani, Kathgodam, Nainital);
        var bhim = TaxiWaysFixture.Row("Bhimtal", Bhimtal, "Nainital", Nainital, 600);
        bhim.GeometryJson = Line(Bhimtal, Nainital);
        var own = TaxiWaysFixture.Row("Haldwani", Haldwani, "Ranikhet", Ranikhet, 3000);
        var anchorRow = TaxiWaysFixture.Row("Haldwani", Haldwani, "Kathgodam", Kathgodam, 400);
        var s = TaxiWaysFixture.Build(through, bhim, own, anchorRow);
        return (s, through.TaxiId, bhim.TaxiId, own.TaxiId);
    }

    [Fact]
    public void A_route_that_starts_elsewhere_but_passes_Haldwani_is_shown_as_a_partial_stretch()
    {
        var (s, through, bhim, _) = Geo();
        var r = TaxiWaysFixture.Search(s, Haldwani, Nainital);
        var ids = Ids(r);
        Assert.Contains(through, ids);
        Assert.DoesNotContain(bhim, ids);
        Assert.True(r.AllTheWay.Single(c => c.Driver.TaxiId == through).Link.Partial);
        Assert.Equal(ids.Count, r.AllTheWay.Count);
    }

    [Fact]
    public void A_traveller_on_the_route_corridor_between_places_is_picked_up_along_the_route()
    {
        var (s, through, bhim, own) = Geo();
        var o = TaxiWaysFixture.Options;
        var a = TaxiWaysSearchEngine.Resolve(s, MidRoad.Lat, MidRoad.Lng, false, o);
        var b = TaxiWaysSearchEngine.Resolve(s, Nainital.Lat, Nainital.Lng, false, o);
        Assert.False(a.Resolved);
        var ids = Ids(TaxiWaysSearchEngine.Search(s, a, b, 4, o));
        Assert.Equal(new HashSet<Guid> { through }, ids);
        Assert.DoesNotContain(bhim, ids);
        Assert.DoesNotContain(own, ids);
    }

    [Fact]
    public void A_point_off_every_route_with_no_geometry_gets_nothing_not_a_radius_sweep()
    {
        var n = Build();
        var o = TaxiWaysFixture.Options;
        foreach (var my in new[] { false, true })
        {
            var a = TaxiWaysSearchEngine.Resolve(n.S, HaldwaniOutside.Lat, HaldwaniOutside.Lng, my, o);
            var b = TaxiWaysSearchEngine.Resolve(n.S, Nainital.Lat, Nainital.Lng, false, o);
            Assert.False(a.Resolved);
            Assert.Empty(TaxiWaysSearchEngine.Search(n.S, a, b, 4, o).AllTheWay);
        }
    }

    [Fact]
    public void One_direct_car_is_never_also_listed_as_a_two_car_plan()
    {
        var (s, through, _, _) = Geo();
        var r = TaxiWaysFixture.Search(s, Haldwani, Nainital);
        Assert.DoesNotContain(r.Plans, p => p.Legs.Any(l => l.Drivers.Any(c => c.Driver.TaxiId == through)));
        Assert.Equal(r.AllTheWay.Count, r.AllTheWay.Select(c => c.Driver.TaxiId).Distinct().Count());
    }

    [Fact]
    public void Delhi_to_Almora_still_chains_through_Haldwani_with_two_cars()
    {
        var n = Build();
        var r = TaxiWaysFixture.Search(n.S, Delhi, Almora);
        Assert.Empty(r.AllTheWay);
        var plan = Assert.Single(r.Plans);
        Assert.Equal(2, plan.Cars);
        Assert.Equal(n.DelhiHaldwani, plan.Legs[0].Top.Driver.TaxiId);
        Assert.Equal(n.HaldwaniAlmora, plan.Legs[1].Top.Driver.TaxiId);
    }
}
