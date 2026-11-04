namespace RentNearBy.Tests.Trips;

public class TaxiWaysClusteringTests
{
    private static readonly (double Lat, double Lng) Haridwar = TaxiWaysFixture.Haridwar;
    private static readonly (double Lat, double Lng) Delhi = TaxiWaysFixture.Delhi;
    private static readonly (double Lat, double Lng) Badrinath = TaxiWaysFixture.Badrinath;

    private static TripCityInfo City(long key, string name, (double Lat, double Lng) at) =>
        new() { CityKey = key, Name = name, Latitude = at.Lat, Longitude = at.Lng };

    [Fact]
    public void Route_ends_close_to_an_atlas_city_join_that_city()
    {
        var rows = new[]
        {
            TaxiWaysFixture.Row("Haridwar", Haridwar, "Badrinath", Badrinath, 9800),
            TaxiWaysFixture.Row("Haridwar Junction", (Haridwar.Lat + 0.01, Haridwar.Lng + 0.01), "Delhi", Delhi, 4200),
        };

        var s = TaxiWaysClustering.Build(rows, [City(210000001, "Haridwar", Haridwar)], TaxiWaysFixture.Options, "t", DateTime.UtcNow);

        Assert.True(s.Places.ContainsKey(TaxiWaysKeys.PlaceKeyAtlas(210000001)));
        Assert.Equal(2, s.Links.Count(l => l.Touches(TaxiWaysKeys.PlaceKeyAtlas(210000001))));
    }

    [Fact]
    public void Ends_without_an_atlas_city_still_group_within_the_same_place_radius()
    {
        var rows = new[]
        {
            TaxiWaysFixture.Row("Haridwar", Haridwar, "Badrinath", Badrinath, 9800),
            TaxiWaysFixture.Row("Haridwar", (Haridwar.Lat + 0.02, Haridwar.Lng), "Delhi", Delhi, 4200),
        };

        var s = TaxiWaysClustering.Build(rows, [], TaxiWaysFixture.Options, "t", DateTime.UtcNow);

        Assert.Equal(3, s.Places.Count);
    }

    [Fact]
    public void Far_apart_ends_stay_separate_places()
    {
        var rows = new[]
        {
            TaxiWaysFixture.Row("Delhi", Delhi, "Haridwar", Haridwar, 4200),
            TaxiWaysFixture.Row("Badrinath", Badrinath, "Rishikesh", TaxiWaysFixture.Rishikesh, 8000),
        };

        var s = TaxiWaysClustering.Build(rows, [], TaxiWaysFixture.Options, "t", DateTime.UtcNow);

        Assert.Equal(4, s.Places.Count);
    }

    [Fact]
    public void Every_route_becomes_one_link_and_every_driver_is_kept()
    {
        var rows = new[]
        {
            TaxiWaysFixture.Row("Delhi", Delhi, "Haridwar", Haridwar, 4200),
            TaxiWaysFixture.Row("Haridwar", Haridwar, "Badrinath", Badrinath, 9800),
        };

        var s = TaxiWaysClustering.Build(rows, [], TaxiWaysFixture.Options, "t", DateTime.UtcNow);

        Assert.Equal(2, s.Links.Count);
        Assert.Equal(2, s.Drivers.Count);
        Assert.Equal("t", s.Version);
    }

    [Fact]
    public void An_empty_network_builds_without_error()
    {
        var s = TaxiWaysClustering.Build([], [], TaxiWaysFixture.Options, "t", DateTime.UtcNow);

        Assert.Empty(s.Places);
        Assert.Empty(s.Links);
    }

    [Fact]
    public void Atlas_cities_with_bad_coordinates_are_ignored()
    {
        var bad = City(210000002, "Nowhere", (95, 200));
        var rows = new[] { TaxiWaysFixture.Row("Delhi", Delhi, "Haridwar", Haridwar, 4200) };

        var s = TaxiWaysClustering.Build(rows, [bad], TaxiWaysFixture.Options, "t", DateTime.UtcNow);

        Assert.False(s.Places.ContainsKey(TaxiWaysKeys.PlaceKeyAtlas(210000002)));
    }
}
