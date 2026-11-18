using Microsoft.EntityFrameworkCore;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.TaxiRequests;

namespace RentNearBy.Tests.TaxiRequests;

// Change tracking only, no connection: what the UPDATE of a route save writes.
public class TaxiRouteKeySaveTests
{
    private static readonly TripCityInfo Haldwani = TaxiRouteCityKeyTests.Haldwani;
    private static readonly TripCityInfo Bhimtal = TaxiRouteCityKeyTests.Bhimtal;

    private static ApplicationDbContext NewContext() => new(new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseNpgsql("Host=localhost;Database=design_only", o => o.UseNetTopologySuite())
        .Options);

    private sealed class NeverAnswers : ICityResolver
    {
        public double BoxKm => 10;
        public Task<CityPin> ResolveAsync(double lat, double lng) => new TaskCompletionSource<CityPin>().Task;
        public Task<TrustedCityPin> ResolveTrustedAsync(double lat, double lng) => new TaskCompletionSource<TrustedCityPin>().Task;
        public Task<CityPin> FindAsync(long cityKey) => new TaskCompletionSource<CityPin>().Task;
        public Task<IReadOnlyList<T>> FilterAsync<T>(IEnumerable<T> items, Func<T, (double Lat, double Lng)?> point, long cityKey)
            => Task.FromResult<IReadOnlyList<T>>([]);
        public Task WarmAsync(double lat, double lng) => Task.CompletedTask;
    }

    // Loaded at point A with no key (as UpsertRoutes reads it), then moved to point B.
    private static TaxiRoute LoadedAtA(ApplicationDbContext db)
    {
        var route = new TaxiRoute
        {
            Id = Guid.NewGuid(), TaxiListingId = Guid.NewGuid(), RouteIndex = 1,
            FromName = "Haldwani", ToName = "Haldwani", TripType = "OneWay", Fare = 500,
            FromLatitude = (decimal)Haldwani.Latitude, FromLongitude = (decimal)Haldwani.Longitude,
            ToLatitude = (decimal)Haldwani.Latitude, ToLongitude = (decimal)Haldwani.Longitude,
        };
        db.TaxiRoutes.Attach(route);
        return route;
    }

    [Fact]
    public async Task Backfill_write_between_load_and_save_is_overwritten_when_the_lookup_is_unanswered()
    {
        using var db = NewContext();
        var route = LoadedAtA(db);
        var before = RouteEndsBefore.Of(route);
        // Meanwhile the backfill stores K(A) in the database; the tracked row still holds null.
        route.ToLatitude = (decimal)Bhimtal.Latitude;
        route.ToLongitude = (decimal)Bhimtal.Longitude;

        await TaxiRouteCityKeys.ResolveAsync(new NeverAnswers(), [(route, before)], new RecordingLogger(), TimeSpan.FromMilliseconds(50))
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Null(route.ToCityKey);

        // Without the mark EF sees null == null and leaves the column out, so K(A) would survive next to point B.
        Assert.False(db.Entry(route).Property(r => r.ToCityKey).IsModified);

        TaxiRouteCityKeys.MarkKeysWritten(db, [route]);

        var entry = db.Entry(route);
        Assert.True(entry.Property(r => r.FromCityKey).IsModified);
        Assert.True(entry.Property(r => r.ToCityKey).IsModified);
        Assert.Null(entry.Property(r => r.FromCityKey).CurrentValue);
        Assert.Null(entry.Property(r => r.ToCityKey).CurrentValue);
        Assert.Equal(EntityState.Modified, entry.State);
    }

    [Fact]
    public void Kept_key_is_written_too()
    {
        using var db = NewContext();
        var route = LoadedAtA(db);
        db.Entry(route).Property(r => r.FromCityKey).OriginalValue = Haldwani.CityKey;
        db.Entry(route).Property(r => r.FromCityKey).CurrentValue = Haldwani.CityKey;
        db.Entry(route).Property(r => r.FromCityKey).IsModified = false;

        TaxiRouteCityKeys.MarkKeysWritten(db, [route]);

        Assert.True(db.Entry(route).Property(r => r.FromCityKey).IsModified);
        Assert.Equal(Haldwani.CityKey, db.Entry(route).Property(r => r.FromCityKey).CurrentValue);
    }

    [Fact]
    public void New_and_removed_routes_are_left_alone()
    {
        using var db = NewContext();
        var added = new TaxiRoute { Id = Guid.NewGuid(), TaxiListingId = Guid.NewGuid(), RouteIndex = 2, FromName = "a", ToName = "b", TripType = "OneWay" };
        db.TaxiRoutes.Add(added);
        var removed = LoadedAtA(db);
        db.TaxiRoutes.Remove(removed);

        TaxiRouteCityKeys.MarkKeysWritten(db, [added, removed]);

        Assert.Equal(EntityState.Added, db.Entry(added).State);
        Assert.Equal(EntityState.Deleted, db.Entry(removed).State);
    }
}
