namespace RentNearBy.Tests.Trips;

public class TaxiWaysGeoTests
{
    private static readonly List<TaxiWaysPoint> Line =
    [
        new(29.95, 78.16),
        new(30.10, 78.29),
        new(30.22, 78.78),
        new(30.55, 79.56),
        new(30.74, 79.49),
    ];

    [Fact]
    public void GeoJson_line_string_is_read_as_lat_lng()
    {
        var points = TaxiWaysGeo.ParseLineString("{\"type\": \"LineString\", \"coordinates\": [[78.16, 29.95], [79.49, 30.74]]}");
        Assert.NotNull(points);
        Assert.Equal(2, points!.Count);
        Assert.Equal(29.95, points[0].Lat, 5);
        Assert.Equal(78.16, points[0].Lng, 5);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"type\":\"LineString\",\"coordinates\":[[78.1,29.9]]}")]
    [InlineData("{\"type\":\"LineString\",\"coordinates\":[[\"x\",29.9],[78.2,30.0]]}")]
    [InlineData("{\"type\":\"LineString\",\"coordinates\":[[78.1,99.0],[78.2,30.0]]}")]
    public void Unreadable_geometry_is_null(string? json) => Assert.Null(TaxiWaysGeo.ParseLineString(json));

    [Fact]
    public void Point_on_the_line_has_zero_distance_and_a_middle_fraction()
    {
        var cumulative = TaxiWaysGeo.Cumulative(Line);
        var (meters, fraction) = TaxiWaysGeo.PointToPolyline(Line[2], Line, cumulative);
        Assert.True(meters < 1);
        Assert.InRange(fraction, 0.2, 0.8);
    }

    [Fact]
    public void Far_point_is_far()
    {
        var cumulative = TaxiWaysGeo.Cumulative(Line);
        var (meters, _) = TaxiWaysGeo.PointToPolyline(new TaxiWaysPoint(28.61, 77.21), Line, cumulative);
        Assert.True(meters > 100_000);
    }

    [Fact]
    public void Simplify_keeps_ends_and_respects_the_vertex_cap()
    {
        var dense = Enumerable.Range(0, 1000)
            .Select(i => new TaxiWaysPoint(30 + i * 0.001, 78 + Math.Sin(i / 10.0) * 0.01))
            .ToList();
        var simple = TaxiWaysGeo.Simplify(dense, 150, 200);
        Assert.True(simple.Count <= 200);
        Assert.Equal(dense[0], simple[0]);
        Assert.Equal(dense[^1], simple[^1]);
    }

    [Fact]
    public void Slice_covers_the_requested_part_only()
    {
        var cumulative = TaxiWaysGeo.Cumulative(Line);
        var part = TaxiWaysGeo.Slice(Line, cumulative, 0.25, 0.75);
        Assert.True(part.Count >= 2);
        var total = cumulative[^1];
        var sliced = TaxiWaysGeo.Cumulative(part)[^1];
        Assert.InRange(sliced / total, 0.45, 0.55);
    }

    [Fact]
    public void Virtual_keys_round_trip_and_carry_my_location()
    {
        var key = TaxiWaysKeys.VirtualKey(30.12345, 78.98765, myLocation: true);
        Assert.Equal("m:30.123:78.988", key);
        Assert.True(TaxiWaysKeys.IsValidKey(key));
        Assert.True(TaxiWaysKeys.TryParseVirtual(key, out var lat, out var lng, out var my));
        Assert.Equal(30.123, lat, 3);
        Assert.Equal(78.988, lng, 3);
        Assert.True(my);
        Assert.False(TaxiWaysKeys.TryParseVirtual("a210000123", out _, out _, out _));
    }

    [Fact]
    public void Plan_and_leg_keys_parse_back()
    {
        Assert.True(TaxiWaysKeys.TryParsePlanKey("p1~a1~charidwar:29.95:78.16~a3", out var places));
        Assert.Equal(new[] { "a1", "charidwar:29.95:78.16", "a3" }, places);
        Assert.False(TaxiWaysKeys.TryParsePlanKey("p1~a1~a2~a3~a4~a5", out _));
        Assert.False(TaxiWaysKeys.TryParsePlanKey("p1~a1~a1", out _));
        Assert.True(TaxiWaysKeys.TryParseLegKey("l1~a1~a2", out var from, out var to));
        Assert.Equal(("a1", "a2"), (from, to));
        Assert.False(TaxiWaysKeys.TryParseLegKey("l1~a1~a1", out _, out _));
    }

    [Fact]
    public void Cluster_key_is_folded_short_and_valid()
    {
        var key = TaxiWaysKeys.PlaceKeyCluster("Śrīnagar Garhwal Bus Stand Main Road", 30.2234, 78.7801);
        Assert.StartsWith("csrinagar_garhwal_bus_sta", key);
        Assert.EndsWith(":30.22:78.78", key);
        Assert.True(TaxiWaysKeys.IsValidKey(key));
    }
}
