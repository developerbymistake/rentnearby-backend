using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Infrastructure.Services;

namespace RentNearBy.Tests.Trips;

// Owner rule (final): a pin resolves to one city; a driver is shown only when his route starts at, ends at or passes that
// city and the destination comes later on the route (OneWay never against its direction); chain or relay only when no
// single driver covers the trip.
public class TaxiWaysDirectionRuleTests
{
    private static readonly (double Lat, double Lng) Haldwani = (29.2185, 79.5103);
    private static readonly (double Lat, double Lng) Kamalwaganja = (29.2135, 79.4554);
    private static readonly (double Lat, double Lng) Nainital = (29.3878, 79.4581);
    private static readonly (double Lat, double Lng) Kathgodam = (29.2656, 79.5262);
    private static readonly (double Lat, double Lng) PantnagarAirport = (29.0340, 79.4730);
    private static readonly (double Lat, double Lng) Haridwar = TaxiWaysFixture.Haridwar;
    private static readonly (double Lat, double Lng) Delhi = TaxiWaysFixture.Delhi;

    private static TripCityInfo City(long key, string name, (double Lat, double Lng) at) =>
        new() { CityKey = key, Name = name, DistrictName = "Nainital", StateName = "Uttarakhand", Latitude = at.Lat, Longitude = at.Lng };

    // Pantnagar is not live: the airport is 20 km from Haldwani.
    private static readonly TripCityInfo[] Live =
        [City(1, "Haldwani", Haldwani), City(2, "Nainital", Nainital), City(3, "Haridwar", Haridwar), City(4, "Kathgodam", Kathgodam)];

    private static string Line(params (double Lat, double Lng)[] pts) =>
        "{\"type\":\"LineString\",\"coordinates\":[" + string.Join(",", pts.Select(p =>
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"[{p.Lng},{p.Lat}]"))) + "]}";

    private static TaxiWaysSourceRow Row(string from, (double, double) f, string to, (double, double) t, int fare,
        string type = "OneWay", Guid? taxi = null, params (double Lat, double Lng)[] via)
    {
        var r = TaxiWaysFixture.Row(from, (f.Item1, f.Item2), to, (t.Item1, t.Item2), fare, taxi: taxi);
        r.TripType = type;
        r.GeometryJson = Line([(f.Item1, f.Item2), .. via, (t.Item1, t.Item2)]);
        return r;
    }

    private static TaxiWaysNetworkSnapshot Build(params TaxiWaysSourceRow[] rows) =>
        TaxiWaysClustering.Build(rows, Live, TaxiWaysFixture.Options, "t", DateTime.UtcNow);

    private static HashSet<Guid> Ids(TaxiWaysSearchResult r) => r.AllTheWay.Select(c => c.Driver.TaxiId).ToHashSet();

    [Fact]
    public void A_route_end_farther_than_its_own_place_from_every_city_keeps_its_own_place_and_does_not_snap_to_Haldwani()
    {
        var mohan = Row("Nainital", Nainital, "Pantnagar Airport", PantnagarAirport, 2200, via: [Kathgodam, Haldwani]);
        var s = Build(mohan);
        var route = s.Routes[mohan.RouteId];
        Assert.Equal("Pantnagar Airport", s.Places[route.ToPlace].Name);
        Assert.NotEqual("Haldwani", s.Places[route.ToPlace].Name);
    }

    [Fact]
    public void Nainital_to_Pantnagar_Airport_one_way_is_not_offered_Haldwani_to_Nainital()
    {
        var mohan = Row("Nainital", Nainital, "Pantnagar Airport", PantnagarAirport, 2200, via: [Kathgodam, Haldwani]);
        var direct = Row("Haldwani", Haldwani, "Nainital", Nainital, 1500, via: [Kathgodam]);
        var s = Build(mohan, direct);
        foreach (var from in new[] { Haldwani, Kamalwaganja })
        {
            var ids = Ids(TaxiWaysFixture.Search(s, from, Nainital));
            Assert.DoesNotContain(mohan.TaxiId, ids);
            Assert.Contains(direct.TaxiId, ids);
        }
        Assert.Contains(mohan.TaxiId, Ids(TaxiWaysFixture.Search(s, Nainital, Haldwani)));
    }

    [Fact]
    public void Kathgodam_to_Haridwar_is_never_a_Haldwani_to_Nainital_taxi_even_with_a_second_Kathgodam_Nainital_route()
    {
        var sunilId = Guid.NewGuid();
        var haridwar = Row("Kathgodam", Kathgodam, "Haridwar", Haridwar, 5200, taxi: sunilId, via: [Haldwani]);
        var toNainital = Row("Kathgodam", Kathgodam, "Nainital", Nainital, 1200, taxi: sunilId);
        var direct = Row("Haldwani", Haldwani, "Nainital", Nainital, 1500, via: [Kathgodam]);
        foreach (var rows in new[] { new[] { haridwar, toNainital, direct }, new[] { haridwar, toNainital } })
        {
            var s = Build(rows);
            Assert.DoesNotContain(sunilId, Ids(TaxiWaysFixture.Search(s, Haldwani, Nainital)));
            Assert.DoesNotContain(sunilId, Ids(TaxiWaysFixture.Search(s, Kamalwaganja, Nainital)));
        }
    }

    [Fact]
    public void Haldwani_to_Nainital_driver_is_direct_and_a_Delhi_to_Nainital_driver_passing_Haldwani_is_on_the_way()
    {
        var direct = Row("Haldwani", Haldwani, "Nainital", Nainital, 1500, via: [Kathgodam]);
        var delhi = Row("Delhi", Delhi, "Nainital", Nainital, 6000, via: [Haldwani, Kathgodam]);
        var s = Build(direct, delhi);
        var r = TaxiWaysFixture.Search(s, Haldwani, Nainital);

        var d = TaxiWaysMapper.Driver(s, r.AllTheWay.Single(c => c.Driver.TaxiId == direct.TaxiId));
        Assert.Equal(WayDriverSections.Direct, d.Section);
        Assert.Equal("Haldwani", d.FromName);
        Assert.Equal("Nainital", d.ToName);
        Assert.False(d.Partial);

        var o = TaxiWaysMapper.Driver(s, r.AllTheWay.Single(c => c.Driver.TaxiId == delhi.TaxiId));
        Assert.Equal(WayDriverSections.OnTheWay, o.Section);
        Assert.Equal("Haldwani", o.FromName);
        Assert.Equal("Nainital", o.ToName);
        Assert.Equal("Delhi", o.RouteFromName);
        Assert.Equal("Nainital", o.RouteToName);
        Assert.True(o.Partial);
        Assert.True(o.FareEstimated);
        Assert.NotNull(o.EstimatedFare);
    }

    [Fact]
    public void A_one_way_Nainital_to_Haldwani_route_is_not_offered_Haldwani_to_Nainital_but_a_round_trip_is()
    {
        var one = Row("Nainital", Nainital, "Haldwani", Haldwani, 1500, via: [Kathgodam]);
        var round = Row("Nainital", Nainital, "Haldwani", Haldwani, 1700, type: "RoundTrip", via: [Kathgodam]);
        var s = Build(one, round);
        var ids = Ids(TaxiWaysFixture.Search(s, Haldwani, Nainital));
        Assert.DoesNotContain(one.TaxiId, ids);
        Assert.Contains(round.TaxiId, ids);
        Assert.Contains(one.TaxiId, Ids(TaxiWaysFixture.Search(s, Nainital, Haldwani)));
    }

    [Fact]
    public void A_relay_moving_toward_the_destination_is_offered_only_when_nobody_covers_the_trip()
    {
        var id = Guid.NewGuid();
        var first = Row("Delhi", Delhi, "Haridwar", Haridwar, 4000, taxi: id);
        var second = Row("Haridwar", Haridwar, "Kathgodam", Kathgodam, 3000, taxi: id);
        var s = Build(first, second);
        Assert.Contains(id, Ids(TaxiWaysFixture.Search(s, Delhi, Kathgodam)));

        var direct = Row("Delhi", Delhi, "Kathgodam", Kathgodam, 6500);
        var withDirect = Build(first, second, direct);
        var ids = Ids(TaxiWaysFixture.Search(withDirect, Delhi, Kathgodam));
        Assert.Equal(new HashSet<Guid> { direct.TaxiId }, ids);
    }

    [Fact]
    public void A_relay_whose_first_stretch_goes_backward_is_never_offered()
    {
        var id = Guid.NewGuid();
        // A Haridwar based car: Haridwar -> Delhi (away from Nainital), then Delhi -> Nainital.
        var back = Row("Haridwar", Haridwar, "Delhi", Delhi, 4000, type: "RoundTrip", taxi: id);
        var onward = Row("Delhi", Delhi, "Nainital", Nainital, 6500, taxi: id);
        var s = Build(back, onward);
        Assert.DoesNotContain(id, Ids(TaxiWaysFixture.Search(s, Haridwar, Nainital)));
    }
}
