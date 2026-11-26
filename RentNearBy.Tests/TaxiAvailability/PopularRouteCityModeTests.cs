using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Entities.Owner;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Repositories;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Support.Buddy;
using RentNearBy.Tests.Support.Owner;

namespace RentNearBy.Tests.TaxiAvailability;

// SPEC 5 (owner: the admin picks two cities, taxis load by city): route-feed with fromCityKey + toCityKey matches drivers by the
// same city-key pair as the popular card, so the card count equals the "Drivers on this route" list.
public class PopularRouteCityModeTests
{
    [Theory]
    [InlineData(null, null, true, false)]
    [InlineData(101L, 245L, true, true)]
    [InlineData(101L, null, false, false)]
    [InlineData(null, 245L, false, false)]
    [InlineData(101L, 101L, false, false)]
    [InlineData(0L, 245L, false, false)]
    [InlineData(-5L, 245L, false, false)]
    public void City_pair_needs_both_positive_different_keys_or_none(long? from, long? to, bool ok, bool hasPair)
    {
        Assert.Equal(ok, TaxiRouteCityPair.TryCreate(from, to, out var pair));
        Assert.Equal(hasPair, pair != null);
        if (hasPair) Assert.Equal(new TaxiRouteCityPair(from!.Value, to!.Value), pair);
    }

    [Fact]
    public void City_mode_sql_matches_by_city_keys_in_either_direction_with_the_rule_and_no_pin_radius()
    {
        foreach (var sql in new[]
                 {
                     TaxiListingRepository.RouteSearchSql(withDay: true, cheapest: false, byCity: true),
                     TaxiListingRepository.RouteSearchSql(withDay: true, cheapest: true, byCity: true),
                     TaxiListingRepository.RouteFreeCountSql(withDay: true, byCity: true),
                 })
        {
            Assert.Contains("(r.\"ToCityKey\" = @toCity AND r.\"FromCityKey\" = @fromCity)", sql);
            Assert.Contains("(r.\"ToCityKey\" = @fromCity AND r.\"FromCityKey\" = @toCity)", sql);
            Assert.Contains("NOT r.\"HiddenByPlan\"", sql);
            Assert.Contains(TaxiAvailabilitySql.FreeOn("t", "@day"), sql);
            Assert.DoesNotContain("ST_DWithin", sql);
            Assert.DoesNotContain("@radius", sql);
        }
        Assert.Contains("ST_DWithin", TaxiListingRepository.RouteSearchSql(true, false));
        Assert.DoesNotContain("@fromCity", TaxiListingRepository.RouteSearchSql(true, false));
    }

    [Fact]
    public void City_mode_uses_the_popular_card_pair_predicate()
    {
        // Same pair test as PopularRouteReader.Sql, with the route's keys in place of the PopularRoutes columns.
        Assert.Contains("(r.\"ToCityKey\" = p.\"ToCityKey\" AND r.\"FromCityKey\" = p.\"FromCityKey\")", PopularRouteReader.Sql);
        Assert.Contains("(r.\"ToCityKey\" = p.\"FromCityKey\" AND r.\"FromCityKey\" = p.\"ToCityKey\")", PopularRouteReader.Sql);
        Assert.Contains("r.\"ToCityKey\" IS NOT NULL AND r.\"FromCityKey\" IS NOT NULL", PopularRouteReader.Sql);
        Assert.Contains("r.\"ToCityKey\" IS NOT NULL AND r.\"FromCityKey\" IS NOT NULL", TaxiListingRepository.RouteSearchSql(true, false, true));
    }

    [Fact]
    public void City_mode_sends_only_the_city_parameters()
    {
        var names = TaxiListingRepository.RouteParameters(1, 2, 3, 4, null, new DateOnly(2026, 11, 26), new TaxiRouteCityPair(101, 245))
            .Select(p => p.ParameterName).ToList();
        Assert.Equal(new[] { "fromCity", "toCity", "category", "day" }, names);
        var pin = TaxiListingRepository.RouteParameters(1, 2, 3, 4, null, null).Select(p => p.ParameterName).ToList();
        Assert.Equal(new[] { "fromLat", "fromLng", "toLat", "toLng", "radius", "category" }, pin);
    }
}

// Needs RENTNEARBY_TEST_DB (PostGIS) and the regenerated InitialCreate; WRITTEN, NOT RUN on this machine.
public class PopularRouteCityModeIntegrationTests(TestDatabase db) : IntegrationTestBase(db)
{
    private static DateOnly TodayIst => IstTime.TodayIst(DateTime.UtcNow);

    private static async Task<Guid> VehicleTypeAsync(ApplicationDbContext c)
    {
        var id = Guid.NewGuid();
        c.Set<TaxiVehicleType>().Add(new TaxiVehicleType { Id = id, Name = "City mode " + id.ToString("N")[..8], Category = "Car", CreatedAt = DateTime.UtcNow });
        await c.SaveChangesAsync();
        return id;
    }

    private static async Task<Guid> DriverAsync(
        ApplicationDbContext c, Guid vt, (double Lat, double Lng) from, (double Lat, double Lng) to, long? fromKey, long? toKey, int fare)
    {
        var owner = await OwnerSeed.TaxiDriverAsync(c);
        await c.TaxiListings.Where(t => t.Id == owner.ListingId).ExecuteUpdateAsync(s => s
            .SetProperty(t => t.VehicleTypeId, (Guid?)vt)
            .SetProperty(t => t.Latitude, (decimal?)from.Lat)
            .SetProperty(t => t.Longitude, (decimal?)from.Lng)
            .SetProperty(t => t.MinFare, (int?)fare)
            .SetProperty(t => t.IsAvailable, true));
        c.TaxiRoutes.Add(new TaxiRoute
        {
            Id = Guid.NewGuid(), TaxiListingId = owner.ListingId, RouteIndex = 0, FromName = "A", ToName = "B",
            FromLatitude = (decimal)from.Lat, FromLongitude = (decimal)from.Lng, ToLatitude = (decimal)to.Lat, ToLongitude = (decimal)to.Lng,
            FromCityKey = fromKey, ToCityKey = toKey,
            Fare = fare, TripType = TaxiTripTypes.OneWay, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await c.SaveChangesAsync();
        return owner.ListingId;
    }

    [IntegrationFact]
    public async Task Popular_card_count_equals_the_city_mode_on_route_list_also_for_a_route_end_far_from_the_centre()
    {
        var lat = Math.Round(10 + Random.Shared.NextDouble() * 5, 4);
        var lng = Math.Round(75 + Random.Shared.NextDouble() * 5, 4);
        var fromCentre = (lat, lng);
        var toCentre = (lat + 0.5, lng + 0.5);
        var fromKey = 900_000_000L + Random.Shared.Next(1, 90_000_000);
        var toKey = fromKey + 1;
        var today = TodayIst;
        var routeId = Guid.NewGuid();

        Guid near, far, reversed, off, noKeys;
        await using (var c = CreateContext())
        {
            var vt = await VehicleTypeAsync(c);
            near = await DriverAsync(c, vt, fromCentre, toCentre, fromKey, toKey, 1200);
            // Same two cities, but the saved end is about 8 km from the centre pin (outside the 5 km pin radius).
            far = await DriverAsync(c, vt, fromCentre, (toCentre.Item1 + 0.072, toCentre.Item2), fromKey, toKey, 900);
            reversed = await DriverAsync(c, vt, toCentre, fromCentre, toKey, fromKey, 1500);
            off = await DriverAsync(c, vt, fromCentre, toCentre, fromKey, toKey, 800);
            noKeys = await DriverAsync(c, vt, fromCentre, toCentre, null, null, 700);
            c.Set<TaxiDayOff>().Add(new TaxiDayOff { TaxiListingId = off, Date = today, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            c.Set<PopularRoute>().Add(new PopularRoute
            {
                Id = routeId, FromCityKey = fromKey, ToCityKey = toKey, FromCityName = "From test", ToCityName = "To test",
                SortOrder = 10_000, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            await c.SaveChangesAsync();
        }

        try
        {
            await using var c = CreateContext();
            var repo = new TaxiListingRepository(c);
            var card = (await c.Database.SqlQueryRaw<PopularRouteReader.Row>(PopularRouteReader.Sql, TaxiAvailabilitySql.Day(today)).ToListAsync())
                .Single(r => r.Id == routeId);
            var pair = new TaxiRouteCityPair(fromKey, toKey);

            var list = (await repo.SearchByRoutePagedAsync(fromCentre.lat, fromCentre.lng, toCentre.Item1, toCentre.Item2, null, 1, 50, today, false, pair)).Items;
            var ids = list.Select(x => x.Id).OrderBy(x => x).ToList();
            Assert.Equal(new[] { near, far, reversed }.OrderBy(x => x).ToList(), ids);
            Assert.Equal(card.Taxis, ids.Count);
            Assert.Equal(card.MinFare, list.Min(x => x.MatchedFare));
            Assert.Equal(card.Taxis, await repo.CountRouteFreeAsync(fromCentre.lat, fromCentre.lng, toCentre.Item1, toCentre.Item2, null, today, [], pair));
            Assert.True(list.Single(x => x.Id == reversed).MatchReversed);

            // The pin search misses the far end and counts the keyless route: the gap the city mode closes.
            var byPin = (await repo.SearchByRoutePagedAsync(fromCentre.lat, fromCentre.lng, toCentre.Item1, toCentre.Item2, null, 1, 50, today)).Items.Select(x => x.Id).ToList();
            Assert.DoesNotContain(far, byPin);
            Assert.Contains(noKeys, byPin);
        }
        finally
        {
            await using var c = CreateContext();
            await c.Set<PopularRoute>().Where(p => p.Id == routeId).ExecuteDeleteAsync();
        }
    }

    [IntegrationTheory]
    [InlineData(3)]
    [InlineData(30)]
    public async Task City_mode_reads_are_one_statement_each_whatever_the_number_of_taxis(int taxis)
    {
        var lat = Math.Round(10 + Random.Shared.NextDouble() * 5, 4);
        var lng = Math.Round(75 + Random.Shared.NextDouble() * 5, 4);
        var fromKey = 900_000_000L + Random.Shared.Next(1, 90_000_000);
        var pair = new TaxiRouteCityPair(fromKey, fromKey + 1);
        var d = TodayIst.AddDays(1);
        await using (var c = CreateContext())
        {
            var vt = await VehicleTypeAsync(c);
            for (var i = 0; i < taxis; i++)
                await DriverAsync(c, vt, (lat, lng), (lat + 0.5, lng + 0.5), pair.FromCityKey, pair.ToCityKey, 1000 + i);
        }
        await using var counted = Db.CreateCountingContext(out var counter);
        var repo = new TaxiListingRepository(counted);
        var page = await repo.SearchByRoutePagedAsync(lat, lng, lat + 0.5, lng + 0.5, null, 1, 20, d, false, pair);
        Assert.Equal(1, counter.Count);
        Assert.Equal(Math.Min(taxis, 20), page.Items.Count);
        counter.Reset();
        Assert.Equal(taxis, await repo.CountRouteFreeAsync(lat, lng, lat + 0.5, lng + 0.5, null, d, [], pair));
        Assert.Equal(1, counter.Count);
    }
}
