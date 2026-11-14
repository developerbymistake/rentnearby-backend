namespace RentNearBy.Tests.Trips;

internal static class TaxiWaysFixture
{
    public static readonly TaxiWaysOptions Options = new();
    public static readonly (double Lat, double Lng) Delhi = (28.6139, 77.2090);
    public static readonly (double Lat, double Lng) Haridwar = (29.9457, 78.1642);
    public static readonly (double Lat, double Lng) Rishikesh = (30.0869, 78.2676);
    public static readonly (double Lat, double Lng) Badrinath = (30.7433, 79.4938);
    public static readonly (double Lat, double Lng) Lucknow = (26.8467, 80.9462);
    public static readonly (double Lat, double Lng) Kathgodam = (29.2656, 79.5262);

    private static int _n;

    public static TaxiWaysSourceRow Row(string fromName, (double Lat, double Lng) from, string toName, (double Lat, double Lng) to,
        int fare, short seats = 6, short rank = 0, Guid? taxi = null, string label = "new")
    {
        _n++;
        return new TaxiWaysSourceRow
        {
            RouteId = Guid.NewGuid(),
            TaxiId = taxi ?? Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            DriverName = "Driver " + _n,
            Seats = seats,
            PriorityRank = rank,
            TripReplyLabel = label,
            FromName = fromName,
            FromLat = from.Lat,
            FromLng = from.Lng,
            ToName = toName,
            ToLat = to.Lat,
            ToLng = to.Lng,
            Fare = fare,
        };
    }

    public static TaxiWaysNetworkSnapshot Build(params TaxiWaysSourceRow[] rows) =>
        TaxiWaysClustering.Build(rows, [], Options, "t", DateTime.UtcNow);

    public static TaxiWaysSearchResult Search(TaxiWaysNetworkSnapshot s, (double Lat, double Lng) from, (double Lat, double Lng) to, int people = 4)
    {
        var a = TaxiWaysSearchEngine.Resolve(s, from.Lat, from.Lng, false, Options);
        var b = TaxiWaysSearchEngine.Resolve(s, to.Lat, to.Lng, false, Options);
        return TaxiWaysSearchEngine.Search(s, a, b, people, Options);
    }
}

public class TaxiWaysSearchEngineTests
{
    private static readonly (double Lat, double Lng) Delhi = TaxiWaysFixture.Delhi;
    private static readonly (double Lat, double Lng) Haridwar = TaxiWaysFixture.Haridwar;
    private static readonly (double Lat, double Lng) Rishikesh = TaxiWaysFixture.Rishikesh;
    private static readonly (double Lat, double Lng) Badrinath = TaxiWaysFixture.Badrinath;
    private static readonly (double Lat, double Lng) Lucknow = TaxiWaysFixture.Lucknow;
    private static readonly (double Lat, double Lng) Kathgodam = TaxiWaysFixture.Kathgodam;

    [Fact]
    public void Direct_driver_is_found_and_no_plan_is_needed()
    {
        var s = TaxiWaysFixture.Build(TaxiWaysFixture.Row("Delhi", Delhi, "Badrinath", Badrinath, 16500));

        var r = TaxiWaysFixture.Search(s, Delhi, Badrinath);

        Assert.Single(r.AllTheWay);
        Assert.Equal(16500, r.AllTheWay[0].Link.Fare);
    }

    [Fact]
    public void A_round_trip_driver_works_in_the_reverse_direction()
    {
        var row = TaxiWaysFixture.Row("Badrinath", Badrinath, "Delhi", Delhi, 17000);
        row.TripType = "RoundTrip";
        var s = TaxiWaysFixture.Build(row);

        var r = TaxiWaysFixture.Search(s, Delhi, Badrinath);

        Assert.Single(r.AllTheWay);
    }

    [Fact]
    public void Two_cars_are_joined_when_nobody_goes_all_the_way()
    {
        var s = TaxiWaysFixture.Build(
            TaxiWaysFixture.Row("Delhi", Delhi, "Haridwar", Haridwar, 4200),
            TaxiWaysFixture.Row("Haridwar", Haridwar, "Badrinath", Badrinath, 9800));

        var r = TaxiWaysFixture.Search(s, Delhi, Badrinath);

        Assert.Empty(r.AllTheWay);
        var plan = Assert.Single(r.Plans);
        Assert.Equal(2, plan.Cars);
        Assert.Equal(14000, plan.Total);
        Assert.Equal(4200, plan.Legs[0].ListedFare);
        Assert.Equal(9800, plan.Legs[1].ListedFare);
    }

    [Fact]
    public void Cheaper_two_car_plan_is_listed_first()
    {
        var s = TaxiWaysFixture.Build(
            TaxiWaysFixture.Row("Delhi", Delhi, "Haridwar", Haridwar, 4200),
            TaxiWaysFixture.Row("Haridwar", Haridwar, "Badrinath", Badrinath, 9800),
            TaxiWaysFixture.Row("Delhi", Delhi, "Rishikesh", Rishikesh, 5600),
            TaxiWaysFixture.Row("Rishikesh", Rishikesh, "Badrinath", Badrinath, 8600));

        var r = TaxiWaysFixture.Search(s, Delhi, Badrinath);

        Assert.Equal(2, r.Plans.Count);
        Assert.Equal(14000, r.Plans[0].Total);
        Assert.Equal(14200, r.Plans[1].Total);
    }

    [Fact]
    public void Three_cars_are_used_when_no_two_car_plan_exists()
    {
        var s = TaxiWaysFixture.Build(
            TaxiWaysFixture.Row("Lucknow", Lucknow, "Kathgodam", Kathgodam, 6500),
            TaxiWaysFixture.Row("Kathgodam", Kathgodam, "Haridwar", Haridwar, 5000),
            TaxiWaysFixture.Row("Haridwar", Haridwar, "Badrinath", Badrinath, 9800));

        var r = TaxiWaysFixture.Search(s, Lucknow, Badrinath);

        var plan = Assert.Single(r.Plans);
        Assert.Equal(3, plan.Cars);
        Assert.Equal(21300, plan.Total);
    }

    // On the Delhi to Badrinath road; every hop is over 15 km, brings the traveller closer, and the way stays within 1.5 times the straight line.
    private static readonly (double Lat, double Lng) SrinagarGarhwal = (30.2230, 78.7830);

    [Fact]
    public void A_three_car_plan_cheaper_than_every_two_car_plan_is_kept()
    {
        var s = TaxiWaysFixture.Build(
            TaxiWaysFixture.Row("Delhi", Delhi, "Haridwar", Haridwar, 4200),
            TaxiWaysFixture.Row("Haridwar", Haridwar, "Badrinath", Badrinath, 9800),
            TaxiWaysFixture.Row("Delhi", Delhi, "Rishikesh", Rishikesh, 3000),
            TaxiWaysFixture.Row("Rishikesh", Rishikesh, "Srinagar Garhwal", SrinagarGarhwal, 3000),
            TaxiWaysFixture.Row("Srinagar Garhwal", SrinagarGarhwal, "Badrinath", Badrinath, 3000));

        var r = TaxiWaysFixture.Search(s, Delhi, Badrinath);

        Assert.Equal(2, r.Plans.Count);
        Assert.Equal((2, 14000), (r.Plans[0].Cars, r.Plans[0].Total));
        Assert.Equal((3, 9000), (r.Plans[1].Cars, r.Plans[1].Total));
    }

    [Fact]
    public void A_three_car_plan_dearer_than_a_two_car_plan_is_dropped()
    {
        var s = TaxiWaysFixture.Build(
            TaxiWaysFixture.Row("Delhi", Delhi, "Haridwar", Haridwar, 4200),
            TaxiWaysFixture.Row("Haridwar", Haridwar, "Badrinath", Badrinath, 9800),
            TaxiWaysFixture.Row("Delhi", Delhi, "Rishikesh", Rishikesh, 5000),
            TaxiWaysFixture.Row("Rishikesh", Rishikesh, "Kathgodam", Kathgodam, 5000),
            TaxiWaysFixture.Row("Kathgodam", Kathgodam, "Badrinath", Badrinath, 5000));

        var r = TaxiWaysFixture.Search(s, Delhi, Badrinath);

        var plan = Assert.Single(r.Plans);
        Assert.Equal((2, 14000), (plan.Cars, plan.Total));
    }

    [Fact]
    public void A_plan_never_exceeds_three_cars()
    {
        var s = TaxiWaysFixture.Build(
            TaxiWaysFixture.Row("Delhi", Delhi, "Haridwar", Haridwar, 1000),
            TaxiWaysFixture.Row("Haridwar", Haridwar, "Rishikesh", Rishikesh, 1000),
            TaxiWaysFixture.Row("Rishikesh", Rishikesh, "Kathgodam", Kathgodam, 1000),
            TaxiWaysFixture.Row("Kathgodam", Kathgodam, "Badrinath", Badrinath, 1000));

        var r = TaxiWaysFixture.Search(s, Delhi, Badrinath);

        Assert.Empty(r.AllTheWay);
        Assert.Empty(r.Plans);
    }

    [Fact]
    public void No_driver_at_all_gives_an_empty_result_not_an_error()
    {
        var s = TaxiWaysFixture.Build(TaxiWaysFixture.Row("Delhi", Delhi, "Haridwar", Haridwar, 4200));

        var r = TaxiWaysFixture.Search(s, Delhi, Badrinath);

        Assert.Empty(r.AllTheWay);
        Assert.Empty(r.Plans);
    }

    [Fact]
    public void Same_from_and_to_gives_nothing()
    {
        var s = TaxiWaysFixture.Build(TaxiWaysFixture.Row("Delhi", Delhi, "Haridwar", Haridwar, 4200));

        var r = TaxiWaysFixture.Search(s, Delhi, Delhi);

        Assert.Empty(r.AllTheWay);
        Assert.Empty(r.Plans);
    }

    [Fact]
    public void One_driver_with_two_chained_routes_is_not_dropped()
    {
        var taxi = Guid.NewGuid();
        var s = TaxiWaysFixture.Build(
            TaxiWaysFixture.Row("Delhi", Delhi, "Haridwar", Haridwar, 4200, taxi: taxi),
            TaxiWaysFixture.Row("Haridwar", Haridwar, "Badrinath", Badrinath, 9800, taxi: taxi));

        var r = TaxiWaysFixture.Search(s, Delhi, Badrinath);

        // One driver on consecutive stretches is one car (B1): offered all the way, fares added, never as two cars.
        Assert.Empty(r.Plans);
        Assert.Equal(14000, Assert.Single(r.AllTheWay).Link.EstimatedFare);
    }
}
