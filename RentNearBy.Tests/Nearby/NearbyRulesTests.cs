namespace RentNearBy.Tests.Nearby;

public class NearbyRulesTests
{
    [Theory]
    [InlineData(0, 0.0)]
    [InlineData(1, 0.1)]
    [InlineData(100, 0.1)]
    [InlineData(101, 0.2)]
    [InlineData(14900, 14.9)]
    [InlineData(14901, 15.0)]
    [InlineData(15000, 15.0)]
    [InlineData(15001, 15.1)]
    [InlineData(420000, 420.0)]
    public void StayKm_rounds_up_to_one_decimal(double meters, double expected)
        => Assert.Equal(expected, NearbyRules.StayKm(meters));

    [Theory]
    [InlineData(0, 5)]
    [InlineData(1, 5)]
    [InlineData(5000, 5)]
    [InlineData(5001, 10)]
    [InlineData(9999, 10)]
    [InlineData(10000, 10)]
    [InlineData(12345, 15)]
    [InlineData(420000, 420)]
    public void TaxiKm_rounds_up_in_five_km_steps_minimum_five(double meters, int expected)
        => Assert.Equal(expected, NearbyRules.TaxiKm(meters));

    [Theory]
    [InlineData(14.9, "Haldwani", "Haldwani", "inCity")]
    [InlineData(14.9, "haldwani ", "Haldwani", "inCity")]
    [InlineData(15.0, "Haldwani", "Haldwani", "inCity")]
    [InlineData(14.9, "Bhimtal", "Haldwani", "near")]
    [InlineData(14.9, null, "Haldwani", "near")]
    [InlineData(14.9, "Haldwani", null, "near")]
    [InlineData(15.1, "Haldwani", "Haldwani", "far")]
    [InlineData(420, "Haldwani", "Delhi", "far")]
    public void LabelKey_follows_distance_and_city(double km, string? item, string? centre, string expected)
        => Assert.Equal(expected, NearbyRules.LabelKey(km, item, centre));

    [Fact]
    public void IsNear_edge_is_15_km()
    {
        Assert.True(NearbyRules.IsNear(15.0));
        Assert.False(NearbyRules.IsNear(15.1));
    }

    [Fact]
    public void LivesIn_line_reads_naturally()
    {
        Assert.Equal("", NearbyRules.LivesInLine([]));
        Assert.Equal("We are live in Uttarakhand", NearbyRules.LivesInLine(["Uttarakhand"]));
        Assert.Equal("We are live in Uttarakhand and Himachal Pradesh",
            NearbyRules.LivesInLine(["Uttarakhand", "Himachal Pradesh"]));
        Assert.Equal("We are live in A, B and C", NearbyRules.LivesInLine(["A", "B", "C"]));
        Assert.Equal("We are live in A and B", NearbyRules.LivesInLine(["A", " ", "a", "B"]));
    }

    [Theory]
    [InlineData(29.2, 79.5, true)]
    [InlineData(-90.0, 180.0, true)]
    [InlineData(90.1, 0.0, false)]
    [InlineData(0.0, -180.1, false)]
    [InlineData(double.NaN, 0.0, false)]
    [InlineData(0.0, double.PositiveInfinity, false)]
    public void ValidLatLng_checks_range(double lat, double lng, bool ok)
        => Assert.Equal(ok, NearbyRules.ValidLatLng(lat, lng));

    [Fact]
    public void ValidLatLng_rejects_null() => Assert.False(NearbyRules.ValidLatLng(null, 10));

    [Fact]
    public void TaxiGridSnap_is_deterministic_and_moves_at_most_one_cell()
    {
        var (la, lo) = NearbyRules.TaxiGridSnap(29.21834, 79.51301);
        Assert.Equal((la, lo), NearbyRules.TaxiGridSnap(29.21834, 79.51301));
        Assert.InRange(Math.Abs(la - 29.21834), 0, 0.009 + 1e-9);
        var haversineM = RentNearBy.Infrastructure.Services.NearbyContext.HaversineKm(29.21834, 79.51301, la, lo) * 1000;
        Assert.InRange(haversineM, 0, NearbyRules.TaxiGridMeters);
    }

    [Fact]
    public void TaxiGridSnap_points_in_one_cell_snap_together()
    {
        var a = NearbyRules.TaxiGridSnap(29.2190, 79.5130);
        var b = NearbyRules.TaxiGridSnap(29.2192, 79.5133);
        Assert.Equal(a, b);
    }
}
