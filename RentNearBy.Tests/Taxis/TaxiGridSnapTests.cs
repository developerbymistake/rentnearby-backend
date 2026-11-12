namespace RentNearBy.Tests.Taxis;

// The SQL of the LocationCoarse column cannot run here; this mirrors it literally and checks the C# rule against it.
public class TaxiGridSnapTests
{
    private const double Step = 0.018;

    private static (double Lat, double Lng) SqlMirror(double lat, double lng)
    {
        var snappedLat = Math.Round(lat / Step) * Step;
        var lngStep = Step / Math.Max(Math.Cos(snappedLat * Math.PI / 180.0), 0.01);
        return (snappedLat, Math.Round(lng / lngStep) * lngStep);
    }

    private static IEnumerable<(double Lat, double Lng)> Points()
    {
        var rnd = new Random(42);
        for (var i = 0; i < 200; i++)
            yield return (Math.Round(rnd.NextDouble() * 160 - 80, 6), Math.Round(rnd.NextDouble() * 358 - 179, 6));
    }

    private static double Km(double la1, double lo1, double la2, double lo2)
    {
        const double R = 6371;
        double Rad(double d) => d * Math.PI / 180;
        var a = Math.Pow(Math.Sin(Rad(la2 - la1) / 2), 2)
            + Math.Cos(Rad(la1)) * Math.Cos(Rad(la2)) * Math.Pow(Math.Sin(Rad(lo2 - lo1) / 2), 2);
        return 2 * R * Math.Asin(Math.Sqrt(a));
    }

    [Fact]
    public void Csharp_rule_matches_the_sql_formula_on_200_points()
    {
        foreach (var (lat, lng) in Points())
        {
            var c = NearbyRules.TaxiGridSnap(lat, lng);
            var s = SqlMirror(lat, lng);
            Assert.Equal(s.Lat, c.Lat, 9);
            Assert.Equal(s.Lng, c.Lng, 9);
        }
    }

    [Fact]
    public void Snapping_is_stable_and_never_moves_a_pin_by_more_than_the_cell()
    {
        foreach (var (lat, lng) in Points().Where(p => Math.Abs(p.Lat) < 70))
        {
            var (sl, so) = NearbyRules.TaxiGridSnap(lat, lng);
            var again = NearbyRules.TaxiGridSnap(sl, so);
            Assert.Equal(sl, again.Lat, 9);
            Assert.Equal(so, again.Lng, 6);
            Assert.True(Km(lat, lng, sl, so) <= 1.8, $"({lat},{lng}) moved too far");
        }
    }

    [Fact]
    public void Nearby_pins_share_a_cell_so_the_exact_pin_is_not_readable()
    {
        var a = NearbyRules.TaxiGridSnap(29.2183, 79.5130);
        var b = NearbyRules.TaxiGridSnap(29.2190, 79.5135);
        Assert.Equal(a, b);
    }

    [Theory, InlineData(0, 5), InlineData(1, 5), InlineData(4999, 5), InlineData(5001, 10), InlineData(15000, 15), InlineData(420100, 425)]
    public void Distance_is_rounded_up_to_5_km_steps(double meters, int km) => Assert.Equal(km, NearbyRules.TaxiKm(meters));
}
