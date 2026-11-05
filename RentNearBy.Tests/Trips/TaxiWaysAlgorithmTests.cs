using System.Globalization;
using RentNearBy.Infrastructure.Repositories;

namespace RentNearBy.Tests.Trips;

public class TaxiFareTests
{
    [Theory]
    [InlineData(6500, 0.89, 5800)]
    [InlineData(6500, 0.11, 700)]
    [InlineData(6500, 1.0, 6500)]
    [InlineData(6500, 0.001, 100)]
    [InlineData(6500, 0.0, 100)]
    [InlineData(80, 0.5, 80)]
    public void A_part_is_priced_by_its_road_share_rounded_to_50(int fare, double span, int expected)
        => Assert.Equal(expected, TaxiFare.ForSpan(fare, span));

    [Fact]
    public void A_link_estimates_only_a_real_part()
    {
        var whole = new TaxiWaysLink { Fare = 6500 };
        var part = new TaxiWaysLink { Fare = 6500, Partial = true, FromFraction = 0.89, ToFraction = 1 };

        Assert.Equal((6500, false), (whole.EstimatedFare, whole.FareEstimated));
        Assert.Equal((700, true), (part.EstimatedFare, part.FareEstimated));
    }

    [Fact]
    public void Refresh_prices_a_stretch_along_the_road_by_its_share()
    {
        var line = new List<(double Lat, double Lon)> { (29.0, 77.0), (29.0, 77.5), (29.0, 78.0) };

        Assert.Equal(3000, SavedJourneyRepository.PartFare(6000, line, 29.0, 77.0, 29.0, 77.5));
        Assert.Equal(6000, SavedJourneyRepository.PartFare(6000, line, 29.0, 77.0, 29.0, 78.0));
    }
}

public class TaxiWaysChainSearchTests
{
    private static string Line(params (double Lat, double Lng)[] points) =>
        "{\"type\":\"LineString\",\"coordinates\":[" + string.Join(",", points.Select(p =>
            string.Create(CultureInfo.InvariantCulture, $"[{p.Lng},{p.Lat}]"))) + "]}";

    [Fact]
    public void The_same_two_drivers_changing_at_two_places_are_one_plan()
    {
        (double, double) a = (29.0, 77.0), b = (29.0, 77.5), c = (29.0, 78.0), d = (29.0, 78.5);
        var r1 = TaxiWaysFixture.Row("A", a, "C", c, 6000);
        r1.GeometryJson = Line(a, b, c);
        var r2 = TaxiWaysFixture.Row("B", b, "D", d, 6000);
        r2.GeometryJson = Line(b, c, d);
        var s = TaxiWaysFixture.Build(r1, r2);

        var r = TaxiWaysFixture.Search(s, a, d);

        Assert.Empty(r.AllTheWay);
        var plan = Assert.Single(r.Plans);
        Assert.Equal(9000, plan.Total);
        Assert.Contains(plan.Legs, l => l.FareEstimated);
    }

    [Fact]
    public void A_chain_through_a_place_with_a_late_key_is_found_without_truncation()
    {
        var o = new TaxiWaysOptions { MaxExpansions = 10 };
        (double, double) hub = (27.0, 80.0), mid = (26.0, 80.0), goal = (25.0, 80.0);
        var rows = new List<TaxiWaysSourceRow>();
        for (var i = 1; i <= 15; i++)
            rows.Add(TaxiWaysFixture.Row("a" + i.ToString("00", CultureInfo.InvariantCulture), (28.0 + i * 0.2, 81.0), "Hub", hub, 1000));
        rows.Add(TaxiWaysFixture.Row("Hub", hub, "zz", mid, 2000));
        rows.Add(TaxiWaysFixture.Row("zz", mid, "Goal", goal, 2000));
        var s = TaxiWaysClustering.Build(rows, [], o, "t", DateTime.UtcNow);

        var from = TaxiWaysSearchEngine.Resolve(s, hub.Item1, hub.Item2, false, o);
        var to = TaxiWaysSearchEngine.Resolve(s, goal.Item1, goal.Item2, false, o);
        var r = TaxiWaysSearchEngine.Search(s, from, to, 2, o);

        Assert.False(r.Truncated);
        var plan = Assert.Single(r.Plans);
        Assert.Equal(4000, plan.Total);
    }

    [Fact]
    public void Plans_are_the_same_whatever_the_route_order()
    {
        var rows = new[]
        {
            TaxiWaysFixture.Row("Delhi", TaxiWaysFixture.Delhi, "Haridwar", TaxiWaysFixture.Haridwar, 4200),
            TaxiWaysFixture.Row("Haridwar", TaxiWaysFixture.Haridwar, "Badrinath", TaxiWaysFixture.Badrinath, 9800),
            TaxiWaysFixture.Row("Delhi", TaxiWaysFixture.Delhi, "Rishikesh", TaxiWaysFixture.Rishikesh, 5600),
            TaxiWaysFixture.Row("Rishikesh", TaxiWaysFixture.Rishikesh, "Badrinath", TaxiWaysFixture.Badrinath, 8600),
        };
        var one = TaxiWaysFixture.Search(TaxiWaysFixture.Build(rows), TaxiWaysFixture.Delhi, TaxiWaysFixture.Badrinath);
        var two = TaxiWaysFixture.Search(TaxiWaysFixture.Build(Enumerable.Reverse(rows).ToArray()), TaxiWaysFixture.Delhi, TaxiWaysFixture.Badrinath);

        Assert.Equal(one.Plans.Select(p => (p.PlanKey, p.Total)), two.Plans.Select(p => (p.PlanKey, p.Total)));
    }
}

public class TaxiWaysPlaceTests
{
    private static double LngAt(double lat, double km) => 79.0 + km / (111.32 * Math.Cos(lat * Math.PI / 180));

    [Fact]
    public void Ends_along_one_road_join_a_fixed_centre_that_every_member_is_near()
    {
        var kms = new[] { 0, 4.6, 7.4, 9.1 };
        var rows = kms.Select((km, i) => TaxiWaysFixture.Row("Road", (29.0, LngAt(29.0, km)), "Far " + i, (24.0 + i, 75.0), 3000)).ToArray();

        var s = TaxiWaysClustering.Build(rows, [], TaxiWaysFixture.Options, "t", DateTime.UtcNow);

        var keys = rows.Select(r => s.Routes[r.RouteId].FromPlace).Distinct().ToList();
        var place = s.Places[Assert.Single(keys)];
        Assert.Equal(LngAt(29.0, 4.6), place.Lng, 9);
        Assert.All(rows, r => Assert.True(TaxiGeo.DistanceMeters(r.FromLat, r.FromLng, place.Lat, place.Lng) <= TaxiWaysFixture.Options.SamePlaceMeters));
    }

    [Fact]
    public void Places_do_not_depend_on_the_order_of_the_routes()
    {
        var kms = new[] { 0, 3, 6, 9, 12, 15 };
        var rows = kms.Select((km, i) => TaxiWaysFixture.Row("Stop " + i, (29.0, LngAt(29.0, km)), "Far " + i, (24.0 + i, 75.0), 3000)).ToArray();

        var a = TaxiWaysClustering.Build(rows, [], TaxiWaysFixture.Options, "t", DateTime.UtcNow);
        var b = TaxiWaysClustering.Build(Enumerable.Reverse(rows).ToArray(), [], TaxiWaysFixture.Options, "t", DateTime.UtcNow);

        Assert.Equal(a.Places.Keys.Order(StringComparer.Ordinal), b.Places.Keys.Order(StringComparer.Ordinal));
        Assert.All(rows, r => Assert.Equal(a.Routes[r.RouteId].FromPlace, b.Routes[r.RouteId].FromPlace));
    }

    [Fact]
    public void A_route_end_between_two_towns_serves_both()
    {
        var haldwani = new TripCityInfo { CityKey = 210000010, Name = "Haldwani", Latitude = 29.218, Longitude = 79.513 };
        var kathgodam = new TripCityInfo { CityKey = 210000011, Name = "Kathgodam", Latitude = 29.267, Longitude = 79.533 };
        var row = TaxiWaysFixture.Row("Delhi", TaxiWaysFixture.Delhi, "Near Kathgodam", (29.250, 79.520), 6500);
        var local = TaxiWaysFixture.Row("Haldwani", (29.218, 79.513), "Bhimtal", (29.35, 79.55), 1500);

        var s = TaxiWaysClustering.Build([row, local], [haldwani, kathgodam], TaxiWaysFixture.Options, "t", DateTime.UtcNow);
        var from = TaxiWaysSearchEngine.Resolve(s, TaxiWaysFixture.Delhi.Lat, TaxiWaysFixture.Delhi.Lng, false, TaxiWaysFixture.Options);
        var toHaldwani = TaxiWaysSearchEngine.Resolve(s, 29.218, 79.513, false, TaxiWaysFixture.Options);
        var r = TaxiWaysSearchEngine.Search(s, from, toHaldwani, 2, TaxiWaysFixture.Options);

        Assert.Equal(TaxiWaysKeys.PlaceKeyAtlas(210000011), s.Routes[row.RouteId].ToPlace);
        Assert.Equal(TaxiWaysKeys.PlaceKeyAtlas(210000010), toHaldwani.Key);
        var driver = Assert.Single(r.AllTheWay);
        Assert.False(driver.Link.Partial);
        Assert.Equal(6500, driver.Link.EstimatedFare);
    }
}

public class TaxiLocalRankingTests
{
    private static TaxiLocalCandidate Taxi(short? seats, int? rate, int? km, short rank = 0, bool inCity = false, double? lat = null, double? lng = null)
        => new(Guid.NewGuid(), seats, rate, km, rank, inCity, lat, lng);

    [Fact]
    public void Seven_people_142_km_rank_the_seven_seater_within_its_km_first()
    {
        var a = Taxi(4, 2500, 80);
        var b = Taxi(7, 3500, 150);

        var ranked = TaxiLocalRanking.Rank([a, b], new TaxiLocalNeed(7, 142));

        Assert.Same(b, ranked[0].Taxi);
        Assert.Equal((true, true, 1, 3500, 0), (ranked[0].Fit.SeatsFit, ranked[0].Fit.KmFit, ranked[0].Fit.Cars, ranked[0].Fit.DayCost!.Value, ranked[0].Fit.ExtraKm!.Value));
        Assert.Equal((false, false, 2, 5000, 62), (ranked[1].Fit.SeatsFit, ranked[1].Fit.KmFit, ranked[1].Fit.Cars, ranked[1].Fit.DayCost!.Value, ranked[1].Fit.ExtraKm!.Value));
    }

    [Fact]
    public void Without_a_need_the_old_order_stays_rank_city_rate()
    {
        var cheap = Taxi(4, 2000, 100);
        var city = Taxi(4, 3000, 100, inCity: true);
        var ranked = Taxi(4, 4000, 100, rank: 2);

        var list = TaxiLocalRanking.Rank([cheap, city, ranked], new TaxiLocalNeed());

        Assert.Equal(new[] { ranked, city, cheap }, list.Select(x => x.Taxi));
        Assert.All(list, x => Assert.Null(x.Fit.ExtraKm));
    }

    [Fact]
    public void Unknown_seats_or_km_limit_never_count_as_a_fit()
    {
        var unknown = Taxi(null, 1000, null);
        var known = Taxi(6, 3000, 200);

        var list = TaxiLocalRanking.Rank([unknown, known], new TaxiLocalNeed(5, 150));

        Assert.Same(known, list[0].Taxi);
        Assert.Null(list[1].Fit.ExtraKm);
    }

    [Fact]
    public void Equal_cost_goes_to_the_nearest_base_then_the_id()
    {
        var far = Taxi(6, 3000, 200, lat: 29.40, lng: 79.10);
        var near = Taxi(6, 3000, 200, lat: 29.39, lng: 79.45);

        var list = TaxiLocalRanking.Rank([far, near], new TaxiLocalNeed(4, 100, 29.38, 79.46));

        Assert.Same(near, list[0].Taxi);
        Assert.True(list[0].Fit.PickupKm < list[1].Fit.PickupKm);
    }
}
