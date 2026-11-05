using RentNearBy.Infrastructure.Data.DemoSeed;

namespace RentNearBy.Tests.Data;

public class NainitalDemoSeederTests
{
    private static TaxiRoute Route(short index, string from, string to, int fare)
        => new() { RouteIndex = index, FromName = from, ToName = to, Fare = fare };

    [Fact]
    public void HasChanges_RouteRewriteOnly_StillSaves()
    {
        Assert.True(NainitalDemoSeeder.HasChanges(0, 0, 1));
        Assert.True(NainitalDemoSeeder.HasChanges(0, 0, 6));
    }

    [Fact]
    public void HasChanges_NothingNew_Skips() => Assert.False(NainitalDemoSeeder.HasChanges(0, 0, 0));

    [Fact]
    public void HasChanges_NewRows_Saves()
    {
        Assert.True(NainitalDemoSeeder.HasChanges(8, 0, 0));
        Assert.True(NainitalDemoSeeder.HasChanges(0, 10, 0));
    }

    [Fact]
    public void RoutesMatch_SameListInAnyStoredOrder_True()
    {
        var stored = new[] { Route(2, "Bhimtal", "Nainital", 900), Route(1, "Nainital", "Kathgodam", 1200) };
        var wanted = new List<(string, string, int)> { ("Nainital", "Kathgodam", 1200), ("Bhimtal", "Nainital", 900) };

        Assert.True(NainitalDemoSeeder.RoutesMatch(stored, wanted));
    }

    [Fact]
    public void RoutesMatch_ChangedFare_False()
    {
        var stored = new[] { Route(1, "Nainital", "Kathgodam", 1200) };
        var wanted = new List<(string, string, int)> { ("Nainital", "Kathgodam", 1300) };

        Assert.False(NainitalDemoSeeder.RoutesMatch(stored, wanted));
    }

    [Fact]
    public void RoutesMatch_LostRoutes_False()
    {
        var wanted = new List<(string, string, int)>
        {
            ("Nainital", "Kathgodam", 1200), ("Nainital", "Bhimtal", 900), ("Nainital", "Mukteshwar", 2500),
            ("Nainital", "Ranikhet", 2800), ("Nainital", "Almora", 3200), ("Nainital", "Kainchi Dham", 1400),
        };

        Assert.False(NainitalDemoSeeder.RoutesMatch(Array.Empty<TaxiRoute>(), wanted));
    }

    [Fact]
    public void RoutesMatch_BothEmpty_True()
        => Assert.True(NainitalDemoSeeder.RoutesMatch(Array.Empty<TaxiRoute>(), new List<(string, string, int)>()));
}
