namespace RentNearBy.Tests.Trips;

public class TaxiRouteGeometryRulesTests
{
    [Fact]
    public void Hash_is_64_hex_and_stable()
    {
        var a = TaxiRouteGeometryRules.EndsHash(29.9457, 78.1642, 30.7433, 79.4938);
        Assert.Equal(64, a.Length);
        Assert.Matches("^[0-9a-f]{64}$", a);
        Assert.Equal(a, TaxiRouteGeometryRules.EndsHash(29.9457, 78.1642, 30.7433, 79.4938));
    }

    [Fact]
    public void Hash_ignores_noise_below_five_decimals()
    {
        Assert.Equal(
            TaxiRouteGeometryRules.EndsHash(29.945700, 78.164200, 30.743300, 79.493800),
            TaxiRouteGeometryRules.EndsHash(29.9457004, 78.1642004, 30.7433004, 79.4938004));
    }

    [Fact]
    public void Hash_changes_when_an_end_moves()
    {
        Assert.NotEqual(
            TaxiRouteGeometryRules.EndsHash(29.9457, 78.1642, 30.7433, 79.4938),
            TaxiRouteGeometryRules.EndsHash(29.9457, 78.1642, 30.7500, 79.4938));
    }

    [Theory]
    [InlineData(100, 100, false)]
    [InlineData(250, 100, false)]
    [InlineData(251, 100, true)]
    [InlineData(80, 100, false)]
    [InlineData(79, 100, true)]
    public void Suspect_bounds(double roadKm, double greatCircleKm, bool suspect) =>
        Assert.Equal(suspect, TaxiRouteGeometryRules.IsSuspect(roadKm, greatCircleKm));

    [Fact]
    public void Constants_match_the_contract()
    {
        Assert.Equal(200, TaxiRouteGeometryRules.MaxVertices);
        Assert.Equal(150, TaxiRouteGeometryRules.SimplifyMetres);
        Assert.Equal(5, TaxiRouteGeometryRules.MaxAttempts);
    }
}
