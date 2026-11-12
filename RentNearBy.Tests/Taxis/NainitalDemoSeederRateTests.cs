using System.Collections;
using System.Reflection;
using RentNearBy.Infrastructure.Data.DemoSeed;

namespace RentNearBy.Tests.Taxis;

// Every seeded taxi must carry the compulsory rate, so a future seed edit cannot reintroduce a taxi without one.
public class NainitalDemoSeederRateTests
{
    private static object[] Seeds()
    {
        var f = typeof(NainitalDemoSeeder).GetField("Taxis", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(f);
        return ((IEnumerable)f!.GetValue(null)!).Cast<object>().ToArray();
    }

    private static T Get<T>(object o, string name) => (T)o.GetType().GetProperty(name)!.GetValue(o)!;

    [Fact]
    public void There_are_seeded_taxis() => Assert.NotEmpty(Seeds());

    [Fact]
    public void Every_seeded_taxi_has_day_rate_and_km_in_range()
    {
        foreach (var t in Seeds())
        {
            var who = Get<string>(t, "Driver");
            Assert.True(TaxiRateRules.RateOk(Get<int?>(t, "DayRate")), $"{who}: day rate missing or out of range");
            Assert.True(TaxiRateRules.KmOk(Get<int?>(t, "DayKm")), $"{who}: day km missing or out of range");
        }
    }

    [Fact]
    public void Every_seeded_taxi_has_routes_with_fares_in_range()
    {
        foreach (var t in Seeds())
        {
            var who = Get<string>(t, "Driver");
            var routes = ((IEnumerable)t.GetType().GetProperty("Routes")!.GetValue(t)!).Cast<object>().ToList();
            Assert.NotEmpty(routes);
            foreach (var r in routes)
            {
                var fare = Get<int>(r, "Fare");
                Assert.True(fare is >= TaxiLimits.MinFare and <= TaxiLimits.MaxFare, $"{who}: route fare {fare} out of range");
            }
        }
    }
}
