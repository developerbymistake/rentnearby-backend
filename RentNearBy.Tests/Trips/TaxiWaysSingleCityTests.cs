using System.Globalization;

namespace RentNearBy.Tests.Trips;

// Owner rule (final): a pin resolves to exactly ONE city, the single nearest Atlas city. Drivers match when their route starts
// at, ends at or passes through that city (and the destination's one city is later on it). No multi-city radius, no car inside
// one city.
public class TaxiWaysSingleCityTests
{
    private static TripCityInfo City(long key, string name, (double Lat, double Lng) at) =>
        new() { CityKey = key, Name = name, Latitude = at.Lat, Longitude = at.Lng };

    private static readonly (double Lat, double Lng) Haldwani = (29.2183, 79.5130);
    private static readonly (double Lat, double Lng) Kathgodam = (29.2656, 79.5262);
    private static readonly (double Lat, double Lng) Nainital = (29.3919, 79.4542);
    private static readonly (double Lat, double Lng) Ranikhet = (29.6434, 79.4322);
    private static readonly (double Lat, double Lng) Bhimtal = (29.3436, 79.5535);
    private static readonly (double Lat, double Lng) Ramnagar = (29.3944, 79.1281);
    private static readonly (double Lat, double Lng) Ramgarh = (29.4167, 79.5667);
    private static readonly (double Lat, double Lng) Almora = (29.5971, 79.6591);
    private static readonly (double Lat, double Lng) Lalkuan = (29.0700, 79.5100);
    private static readonly (double Lat, double Lng) Delhi = TaxiWaysFixture.Delhi;
    // 3 km east of the centre, and 6.2 km out (nearest city still Haldwani; Kathgodam is 7 km away).
    private static readonly (double Lat, double Lng) HaldwaniEast = (29.2183, 79.5420);
    private static readonly (double Lat, double Lng) HaldwaniFarEast = (29.2183, 79.5760);

    private static readonly TripCityInfo[] Cities =
    [
        City(210000001, "Haldwani", Haldwani), City(210000002, "Kathgodam", Kathgodam), City(210000003, "Nainital", Nainital),
        City(210000004, "Ranikhet", Ranikhet), City(210000005, "Bhimtal", Bhimtal), City(210000006, "Ramnagar", Ramnagar),
        City(210000007, "Ramgarh", Ramgarh), City(210000008, "Almora", Almora), City(210000009, "Delhi", Delhi),
        City(210000010, "Lalkuan", Lalkuan),
    ];

    private static string Line(params (double Lat, double Lng)[] pts) =>
        "{\"type\":\"LineString\",\"coordinates\":[" + string.Join(",", pts.Select(p =>
            string.Create(CultureInfo.InvariantCulture, $"[{p.Lng},{p.Lat}]"))) + "]}";

    private static TaxiWaysNetworkSnapshot Build(params TaxiWaysSourceRow[] rows) =>
        TaxiWaysClustering.Build(rows, Cities, TaxiWaysFixture.Options, "t", DateTime.UtcNow);

    private static TaxiWaysSearchResult Search(TaxiWaysNetworkSnapshot s, (double Lat, double Lng) from, (double Lat, double Lng) to) =>
        TaxiWaysFixture.Search(s, from, to);

    private static HashSet<Guid> Ids(TaxiWaysSearchResult r) => r.AllTheWay.Select(c => c.Driver.TaxiId).ToHashSet();

    [Fact]
    public void A_pin_resolves_to_the_single_nearest_atlas_city_even_with_no_route_there()
    {
        var s = Build(TaxiWaysFixture.Row("Bhimtal", Bhimtal, "Nainital", Nainital, 600));
        var a = TaxiWaysSearchEngine.Resolve(s, HaldwaniFarEast.Lat, HaldwaniFarEast.Lng, false, TaxiWaysFixture.Options);
        Assert.True(a.Resolved);
        Assert.Equal(TaxiWaysKeys.PlaceKeyAtlas(210000001), a.Key);
        Assert.Equal("Haldwani", a.Name);
    }

    [Theory]
    [InlineData(29.2183, 79.5130)]
    [InlineData(29.2183, 79.5420)]
    [InlineData(29.2183, 79.5760)]
    public void Anywhere_in_Haldwani_sees_only_Haldwani_routes(double lat, double lng)
    {
        var hn = TaxiWaysFixture.Row("Haldwani", Haldwani, "Nainital", Nainital, 1500);
        var hr = TaxiWaysFixture.Row("Haldwani", HaldwaniEast, "Ranikhet", Ranikhet, 3000);
        var s = Build(hn, hr,
            TaxiWaysFixture.Row("Bhimtal", Bhimtal, "Nainital", Nainital, 600),
            TaxiWaysFixture.Row("Ramnagar", Ramnagar, "Ranikhet", Ranikhet, 2800),
            TaxiWaysFixture.Row("Ramgarh", Ramgarh, "Nainital", Nainital, 700));

        Assert.Equal(new HashSet<Guid> { hn.TaxiId }, Ids(Search(s, (lat, lng), Nainital)));
        Assert.Equal(new HashSet<Guid> { hr.TaxiId }, Ids(Search(s, (lat, lng), Ranikhet)));
    }

    [Fact]
    public void A_route_that_only_passes_through_Haldwani_matches_a_Haldwani_traveller()
    {
        var through = TaxiWaysFixture.Row("Lalkuan", Lalkuan, "Nainital", Nainital, 1800);
        through.GeometryJson = Line(Lalkuan, Haldwani, Kathgodam, Nainital);
        var bhim = TaxiWaysFixture.Row("Bhimtal", Bhimtal, "Nainital", Nainital, 600);
        bhim.GeometryJson = Line(Bhimtal, Nainital);
        var s = Build(through, bhim);

        var r = Search(s, HaldwaniEast, Nainital);

        Assert.Equal(new HashSet<Guid> { through.TaxiId }, Ids(r));
        Assert.True(r.AllTheWay.Single().Link.Partial);
    }

    [Fact]
    public void The_exact_pin_is_used_for_a_route_that_passes_near_it()
    {
        // Atlas has only Haldwani near; the route runs Lalkuan -> Kathgodam -> Nainital and passes 1 km from the pin.
        var through = TaxiWaysFixture.Row("Lalkuan", Lalkuan, "Nainital", Nainital, 1800);
        through.GeometryJson = Line(Lalkuan, (29.2183, 79.5230), Kathgodam, Nainital);
        var s = Build(through);

        var a = TaxiWaysSearchEngine.Resolve(s, HaldwaniEast.Lat, HaldwaniEast.Lng, false, TaxiWaysFixture.Options);

        Assert.Equal(TaxiWaysKeys.PlaceKeyAtlas(210000001), a.Key);
        Assert.NotEmpty(a.Links);
        Assert.Contains(through.TaxiId, Ids(Search(s, HaldwaniEast, Nainital)));
    }

    [Fact]
    public void A_hop_inside_one_city_is_never_a_car()
    {
        var stand = (29.2183, 79.5420);
        var local = TaxiWaysFixture.Row("Haldwani", Haldwani, "Haldwani bus stand", stand, 150);
        var onward = TaxiWaysFixture.Row("Haldwani bus stand", stand, "Nainital", Nainital, 1500);
        var s = Build(local, onward);

        Assert.DoesNotContain(local.RouteId, s.Routes.Keys);
        var r = Search(s, Haldwani, Nainital);
        Assert.Equal(new HashSet<Guid> { onward.TaxiId }, Ids(r));
        Assert.Empty(r.Plans);
    }

    [Fact]
    public void Delhi_to_Almora_still_chains_through_Haldwani()
    {
        var s = Build(
            TaxiWaysFixture.Row("Delhi", Delhi, "Haldwani", Haldwani, 6000),
            TaxiWaysFixture.Row("Haldwani", Haldwani, "Almora", Almora, 3500));

        var r = Search(s, Delhi, Almora);

        Assert.Empty(r.AllTheWay);
        Assert.Equal(2, Assert.Single(r.Plans).Cars);
    }

    private static readonly (double Lat, double Lng) PinNearRoad = (29.2183, 79.5500);

    // A road 1 km from the pin but 4.6 km from the Haldwani centre (so Haldwani is not a stop of it): only the exact pin matches it.
    private static (TaxiWaysNetworkSnapshot S, TaxiWaysSourceRow Row) RoadByThePin()
    {
        var row = TaxiWaysFixture.Row("Road start", (29.0700, 79.5600), "Nainital", Nainital, 1900);
        row.GeometryJson = Line((29.0700, 79.5600), (29.2183, 79.5600), Nainital);
        return (Build(row), row);
    }

    [Fact]
    public void A_leg_key_carries_the_pin_and_a_requery_returns_the_same_drivers()
    {
        var (s, row) = RoadByThePin();
        var o = TaxiWaysFixture.Options;
        var a = TaxiWaysSearchEngine.Resolve(s, PinNearRoad.Lat, PinNearRoad.Lng, false, o);
        var b = TaxiWaysSearchEngine.Resolve(s, Nainital.Lat, Nainital.Lng, false, o);
        Assert.Equal(TaxiWaysKeys.PlaceKeyAtlas(210000001), a.Key);

        var first = TaxiWaysSearchEngine.Search(s, a, b, 4, o);
        Assert.Equal(new HashSet<Guid> { row.TaxiId }, Ids(first));

        var leg = TaxiWaysSearchEngine.BuildLeg(s, a, b, 1, 4, o, true, null)!;
        Assert.True(TaxiWaysKeys.TryParseLegKey(leg.LegKey, out var fromKey, out var toKey));
        var a2 = TaxiWaysSearchEngine.AnchorFromKey(s, fromKey, o)!;
        var b2 = TaxiWaysSearchEngine.AnchorFromKey(s, toKey, o, true)!;
        var again = TaxiWaysSearchEngine.Candidates(s, a2, b2, 4, o, basedHereFirst: true).Select(c => c.Driver.TaxiId).ToList();
        Assert.Equal(new[] { row.TaxiId }, again);

        // The plain city key alone (no pin) cannot even be rebuilt here: proof that the pin is what carries it.
        Assert.Null(TaxiWaysSearchEngine.AnchorFromKey(s, a.Key, o));
    }

    [Fact]
    public void A_pinned_key_is_a_valid_plan_and_leg_key()
    {
        var pinned = TaxiWaysKeys.Pinned(TaxiWaysKeys.PlaceKeyAtlas(210000001), 29.2183, 79.55);
        Assert.Equal("a210000001:p29.218:79.550", pinned);
        Assert.True(TaxiWaysKeys.IsValidKey(TaxiWaysKeys.LegKey(pinned, "a210000003")));
        Assert.True(TaxiWaysKeys.TryParsePlanKey(TaxiWaysKeys.PlanKey([pinned, "a210000005", "a210000003"]), out var keys));
        Assert.Equal(pinned, keys[0]);
        Assert.True(TaxiWaysKeys.TryParsePinned(pinned, out var baseKey, out var lat, out var lng));
        Assert.Equal("a210000001", baseKey);
        Assert.Equal(29.218, lat);
        Assert.Equal(79.55, lng);
        Assert.False(TaxiWaysKeys.TryParsePinned("a210000001", out _, out _, out _));
        Assert.False(TaxiWaysKeys.TryParsePinned("v:29.218:79.550", out _, out _, out _));
    }

    [Fact]
    public void Stays_and_taxis_are_connected_only_when_their_own_nearest_city_is_the_destination_city()
    {
        var s = Build(TaxiWaysFixture.Row("Bhimtal", Bhimtal, "Nainital", Nainital, 600));
        var o = TaxiWaysFixture.Options;
        (double Lat, double Lng)?[] points =
        [
            Nainital,                     // in Nainital
            (29.4000, 79.4600),           // another corner of Nainital
            Bhimtal,                      // another city
            Ramgarh,                      // another city
            Haldwani,                     // another city
            null,                         // no coordinates
        ];
        var kept = TaxiWaysSearchEngine.InCity(s, points, p => p, 210000003, o);
        Assert.Equal(2, kept.Count);
        Assert.All(kept, p => Assert.True(p!.Value.Lat > 29.39));
        Assert.Empty(TaxiWaysSearchEngine.InCity(s, points, p => p, null, o));
        Assert.Empty(TaxiWaysSearchEngine.InCity(TaxiWaysFixture.Build(), points, p => p, 210000003, o));
    }
}
