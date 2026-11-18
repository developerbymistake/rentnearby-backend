using System.Collections;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RentNearBy.Infrastructure.Data;
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

    // Every point resolves to a trusted city whose key is derived from the point, so each end gets its own key.
    private sealed class PointResolver : ICityResolver
    {
        public static long KeyOf(decimal lat, decimal lng) => 210000000 + (long)Math.Round(lat * 100) * 10000 + (long)Math.Round(lng * 100);
        public double BoxKm => 10;
        public Task<CityPin> ResolveAsync(double lat, double lng) => throw new InvalidOperationException("use ResolveTrustedAsync");
        public Task<TrustedCityPin> ResolveTrustedAsync(double lat, double lng) => Task.FromResult(new TrustedCityPin(true,
            new TripCityInfo { CityKey = KeyOf((decimal)lat, (decimal)lng), Name = "C", DistrictName = "D", StateName = "S", Latitude = lat, Longitude = lng }, true));
        public Task<CityPin> FindAsync(long cityKey) => Task.FromResult(new CityPin(true, null));
        public Task<IReadOnlyList<T>> FilterAsync<T>(IEnumerable<T> items, Func<T, (double Lat, double Lng)?> point, long cityKey)
            => Task.FromResult<IReadOnlyList<T>>([]);
        public Task WarmAsync(double lat, double lng) => Task.CompletedTask;
    }

    private static void AssertKeyed(IReadOnlyList<TaxiRoute> routes)
    {
        Assert.NotEmpty(routes);
        foreach (var r in routes)
        {
            Assert.Equal(PointResolver.KeyOf(r.FromLatitude, r.FromLongitude), r.FromCityKey);
            Assert.Equal(PointResolver.KeyOf(r.ToLatitude, r.ToLongitude), r.ToCityKey);
        }
    }

    [Fact]
    public async Task New_seeded_routes_get_both_city_keys_from_the_resolver()
    {
        var routes = NainitalDemoSeeder.BuildRoutes(Guid.NewGuid(), 0, DateTime.UtcNow);

        await NainitalDemoSeeder.KeySeededRoutesAsync(new PointResolver(), routes, NullLogger.Instance);

        var seed = Seeds()[0];
        Assert.Equal(((IEnumerable)seed.GetType().GetProperty("Routes")!.GetValue(seed)!).Cast<object>().Count(), routes.Count);
        AssertKeyed(routes);
    }

    [Fact]
    public async Task Rewritten_seeded_routes_get_both_city_keys_from_the_resolver()
    {
        using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql("Host=localhost;Database=design_only", o => o.UseNetTopologySuite())
            .Options);
        var taxi = new TaxiListing { Id = Guid.NewGuid(), DrivingLicenceNumberEnc = "x", DateOfBirth = new DateOnly(1990, 1, 1) };
        taxi.Routes.Add(new TaxiRoute { Id = Guid.NewGuid(), TaxiListingId = taxi.Id, RouteIndex = 1, FromName = "Old", ToName = "Stale", Fare = 1 });
        var added = new List<TaxiRoute>();

        Assert.True(NainitalDemoSeeder.RewriteRoutes(db, taxi, 0, DateTime.UtcNow, added));
        await NainitalDemoSeeder.KeySeededRoutesAsync(new PointResolver(), added, NullLogger.Instance);

        AssertKeyed(added);
        Assert.All(added, r => Assert.Equal(EntityState.Added, db.Entry(r).State));
    }

    [Fact]
    public async Task Without_a_resolver_seeded_routes_are_left_to_the_backfill()
    {
        var routes = NainitalDemoSeeder.BuildRoutes(Guid.NewGuid(), 0, DateTime.UtcNow);

        await NainitalDemoSeeder.KeySeededRoutesAsync(null, routes, NullLogger.Instance);

        Assert.All(routes, r => { Assert.Null(r.FromCityKey); Assert.Null(r.ToCityKey); });
    }
}
