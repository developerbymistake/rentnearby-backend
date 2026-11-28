using Microsoft.EntityFrameworkCore;
using RentNearBy.Api.Handlers;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Entities.Owner;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Repositories;
using RentNearBy.Infrastructure.Services.Owner;
using RentNearBy.Infrastructure.Services.Owner.Be1;
using RentNearBy.Infrastructure.Services.Owner.Be4;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Owner.Be1;
using RentNearBy.Tests.Owner.Be2;
using RentNearBy.Tests.Support.Buddy;
using RentNearBy.Tests.Support.Owner;

namespace RentNearBy.Tests.TaxiAvailability;

// PLAN_BE section 6 items 20, 23, 27 and R29 on the dated readers: route search, route freeCount, passing busy ids, browse near
// (LINQ twin), money on an off day, Home month bookings. Needs RENTNEARBY_TEST_DB (PostGIS) and the regenerated InitialCreate;
// WRITTEN, NOT RUN on this machine. Each test uses its own random coordinates so rows of other tests never match.
public class TaxiAvailabilityReadIntegrationTests(TestDatabase db) : IntegrationTestBase(db)
{
    private static DateOnly TodayIst => IstTime.TodayIst(DateTime.UtcNow);

    private static OwnerContext Ctx(OwnerSeed.OwnerRow owner)
        => new(owner.UserId, owner.Kind, owner.ListingId, "Approved", null, null, true, null, 0, null, 0,
            Be1Kit.Free(ListingKinds.Taxi), TodayIst, [0, 0, 0, null]);

    private sealed record Place(double FromLat, double FromLng, double ToLat, double ToLng);

    private static Place NewPlace()
    {
        var lat = 10 + Random.Shared.NextDouble() * 5;
        var lng = 75 + Random.Shared.NextDouble() * 5;
        return new Place(Math.Round(lat, 4), Math.Round(lng, 4), Math.Round(lat + 0.5, 4), Math.Round(lng + 0.5, 4));
    }

    private static async Task<Guid> VehicleTypeAsync(ApplicationDbContext c)
    {
        var id = Guid.NewGuid();
        c.Set<TaxiVehicleType>().Add(new TaxiVehicleType { Id = id, Name = "Avail test " + id.ToString("N")[..8], Category = "Car", CreatedAt = DateTime.UtcNow });
        await c.SaveChangesAsync();
        return id;
    }

    // A driver at the From end with one route From -> To.
    private static async Task<OwnerSeed.OwnerRow> RouteTaxiAsync(ApplicationDbContext c, Guid vehicleType, Place p, int fare, bool available = true)
    {
        var owner = await OwnerSeed.TaxiDriverAsync(c);
        await c.TaxiListings.Where(t => t.Id == owner.ListingId).ExecuteUpdateAsync(s => s
            .SetProperty(t => t.VehicleTypeId, (Guid?)vehicleType)
            .SetProperty(t => t.Latitude, (decimal?)p.FromLat)
            .SetProperty(t => t.Longitude, (decimal?)p.FromLng)
            .SetProperty(t => t.MinFare, (int?)fare)
            .SetProperty(t => t.IsAvailable, available));
        c.TaxiRoutes.Add(new TaxiRoute
        {
            Id = Guid.NewGuid(), TaxiListingId = owner.ListingId, RouteIndex = 0, FromName = "A", ToName = "B",
            FromLatitude = (decimal)p.FromLat, FromLongitude = (decimal)p.FromLng, ToLatitude = (decimal)p.ToLat, ToLongitude = (decimal)p.ToLng,
            Fare = fare, TripType = TaxiTripTypes.OneWay, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await c.SaveChangesAsync();
        return owner;
    }

    private static async Task DayOffAsync(ApplicationDbContext c, Guid listingId, DateOnly d)
    {
        c.Set<TaxiDayOff>().Add(new TaxiDayOff { TaxiListingId = listingId, Date = d, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await c.SaveChangesAsync();
    }

    private sealed record Seeded(Place Place, OwnerSeed.OwnerRow Off, OwnerSeed.OwnerRow Booked, OwnerSeed.OwnerRow Manual, OwnerSeed.OwnerRow Free, OwnerSeed.OwnerRow SwitchedOff, DateOnly Day,
        OwnerSeed.OwnerRow Hiding);

    private async Task<Seeded> SeedAsync()
    {
        var p = NewPlace();
        var d = TodayIst.AddDays(2);
        await using var c = CreateContext();
        var vt = await VehicleTypeAsync(c);
        var off = await RouteTaxiAsync(c, vt, p, 1400);
        var booked = await RouteTaxiAsync(c, vt, p, 1300);
        var manual = await RouteTaxiAsync(c, vt, p, 1200);
        var free = await RouteTaxiAsync(c, vt, p, 900);
        var switchedOff = await RouteTaxiAsync(c, vt, p, 800, available: false);
        var hiding = await RouteTaxiAsync(c, vt, p, 1100);
        await DayOffAsync(c, off.ListingId, d);
        await OwnerSeed.AutoEntryAsync(c, booked, CalendarSourceTypes.TaxiBooking, Guid.NewGuid(), d, d);
        // I-25: Manual keeps taking app bookings that day (KeepVisible); Hiding is a plain phone booking and hides the taxi.
        await OwnerSeed.ManualEntriesAsync(c, manual, 1, d, keepVisible: true);
        await OwnerSeed.ManualEntriesAsync(c, hiding, 1, d);
        return new Seeded(p, off, booked, manual, free, switchedOff, d, hiding);
    }

    [IntegrationFact]
    public async Task Route_search_with_a_day_hides_off_and_booked_taxis_on_d_shows_them_on_d_plus_one_and_sorts_cheapest()
    {
        var s = await SeedAsync();
        var p = s.Place;
        await using var c = CreateContext();
        var repo = new TaxiListingRepository(c);

        var onD = (await repo.SearchByRoutePagedAsync(p.FromLat, p.FromLng, p.ToLat, p.ToLng, null, 1, 50, s.Day)).Items.Select(x => x.Id).ToList();
        Assert.DoesNotContain(s.Off.ListingId, onD);
        Assert.DoesNotContain(s.Booked.ListingId, onD);
        Assert.DoesNotContain(s.SwitchedOff.ListingId, onD);
        Assert.Contains(s.Manual.ListingId, onD);
        Assert.DoesNotContain(s.Hiding.ListingId, onD);
        Assert.Contains(s.Free.ListingId, onD);

        var next = (await repo.SearchByRoutePagedAsync(p.FromLat, p.FromLng, p.ToLat, p.ToLng, null, 1, 50, s.Day.AddDays(1))).Items.Select(x => x.Id).ToList();
        Assert.Contains(s.Off.ListingId, next);
        Assert.Contains(s.Booked.ListingId, next);
        Assert.Contains(s.Hiding.ListingId, next);
        Assert.DoesNotContain(s.SwitchedOff.ListingId, next);

        var cheapest = (await repo.SearchByRoutePagedAsync(p.FromLat, p.FromLng, p.ToLat, p.ToLng, null, 1, 50, s.Day.AddDays(1), cheapest: true)).Items;
        var fares = cheapest.Select(x => x.MatchedFare).ToList();
        Assert.Equal(fares.OrderBy(f => f).ToList(), fares);
        Assert.Equal(s.Free.ListingId, cheapest[0].Id);
    }

    [IntegrationFact]
    public async Task Route_free_count_is_distinct_on_route_plus_passing_and_busy_ids_are_the_hidden_ones()
    {
        var s = await SeedAsync();
        var p = s.Place;
        var passing = Guid.NewGuid();
        await using var c = CreateContext();
        var repo = new TaxiListingRepository(c);

        // free + manual on route, plus one passing id and one on-route id again (counted once).
        var count = await repo.CountRouteFreeAsync(p.FromLat, p.FromLng, p.ToLat, p.ToLng, null, s.Day, [passing, s.Free.ListingId]);
        Assert.Equal(3, count);

        var busy = await TaxiFeedHandlers.BusyAsync(c,
            [s.Off.ListingId, s.Booked.ListingId, s.Manual.ListingId, s.Free.ListingId, s.SwitchedOff.ListingId, s.Hiding.ListingId], s.Day, CancellationToken.None);
        Assert.Equal(new[] { s.Off.ListingId, s.Booked.ListingId, s.SwitchedOff.ListingId, s.Hiding.ListingId }.OrderBy(x => x), busy.OrderBy(x => x));
        Assert.Empty(await TaxiFeedHandlers.BusyAsync(c, [s.Off.ListingId, s.Booked.ListingId], s.Day.AddDays(1), CancellationToken.None));
    }

    [IntegrationFact]
    public async Task Browse_near_with_a_day_uses_the_same_rule_and_without_a_day_shows_everyone()
    {
        var s = await SeedAsync();
        var p = s.Place;
        await using var c = CreateContext();
        var repo = new TaxiListingRepository(c);

        var onD = (await repo.BrowseLocalNearAsync(p.FromLat, p.FromLng, 2, false, 50, 0, s.Day)).Select(x => x.Item.Id).ToList();
        Assert.DoesNotContain(s.Off.ListingId, onD);
        Assert.DoesNotContain(s.Booked.ListingId, onD);
        Assert.Contains(s.Manual.ListingId, onD);
        Assert.DoesNotContain(s.Hiding.ListingId, onD);
        Assert.Contains(s.Free.ListingId, onD);

        var undated = (await repo.BrowseLocalNearAsync(p.FromLat, p.FromLng, 2, false, 50)).Select(x => x.Item.Id).ToList();
        Assert.Contains(s.Off.ListingId, undated);
        Assert.Contains(s.Booked.ListingId, undated);
    }

    [IntegrationTheory]
    [InlineData(3)]
    [InlineData(30)]
    public async Task Dated_route_feed_reads_are_one_statement_each_whatever_the_number_of_taxis(int taxis)
    {
        var p = NewPlace();
        var d = TodayIst.AddDays(1);
        var ids = new List<Guid>();
        await using (var c = CreateContext())
        {
            var vt = await VehicleTypeAsync(c);
            for (var i = 0; i < taxis; i++)
            {
                var t = await RouteTaxiAsync(c, vt, p, 1000 + i);
                ids.Add(t.ListingId);
                if (i % 3 == 0) await DayOffAsync(c, t.ListingId, d);
            }
        }
        await using var counted = Db.CreateCountingContext(out var counter);
        var repo = new TaxiListingRepository(counted);
        await repo.SearchByRoutePagedAsync(p.FromLat, p.FromLng, p.ToLat, p.ToLng, null, 1, 20, d);
        Assert.Equal(1, counter.Count);
        counter.Reset();
        await repo.CountRouteFreeAsync(p.FromLat, p.FromLng, p.ToLat, p.ToLng, null, d, ids.Take(2).ToList());
        Assert.Equal(1, counter.Count);
        counter.Reset();
        await TaxiFeedHandlers.BusyAsync(counted, ids, d, CancellationToken.None);
        Assert.Equal(1, counter.Count);
    }

    [IntegrationFact]
    public async Task Money_on_an_entry_whose_day_is_off_is_never_refused_by_the_day_off()
    {
        OwnerSeed.OwnerRow owner;
        CalendarEntry trip;
        await using (var c = CreateContext())
        {
            owner = await OwnerSeed.TaxiDriverAsync(c);
            trip = (await OwnerSeed.ManualEntriesAsync(c, owner, 1, TodayIst))[0];
            await DayOffAsync(c, owner.ListingId, TodayIst);
        }
        await using var db = CreateContext();
        var saved = await new MoneyNoteService(db).SetAsync(Ctx(owner), trip.Id, trip.Version, new MoneyNote(3000, 1000, false));
        Assert.True(saved.Ok);
        Assert.Equal(2000, MoneyNoteMath.Balance(saved.Value!.Money));
    }

    // R29: Home "Bookings" counts what the Bookings tab tile counts from the month read (Auto, not cancelled, DateFrom in the IST month).
    [IntegrationFact]
    public async Task Home_month_bookings_equal_the_auto_rows_of_the_calendar_month_read()
    {
        OwnerSeed.OwnerRow owner;
        var (m1, m2) = TaxiDayOffRules.Month(TodayIst);
        var last = m2.AddDays(-1);
        await using (var c = CreateContext())
        {
            owner = await OwnerSeed.TaxiDriverAsync(c);
            await OwnerSeed.AutoEntryAsync(c, owner, CalendarSourceTypes.TaxiBooking, Guid.NewGuid(), m1, m1);
            await OwnerSeed.AutoEntryAsync(c, owner, CalendarSourceTypes.TaxiBooking, Guid.NewGuid(), last, last);
            await OwnerSeed.AutoEntryAsync(c, owner, CalendarSourceTypes.TaxiBooking, Guid.NewGuid(), last, last, cancelled: true);
            await OwnerSeed.AutoEntryAsync(c, owner, CalendarSourceTypes.TaxiBooking, Guid.NewGuid(), m1.AddDays(-2), m1);
            await OwnerSeed.ManualEntriesAsync(c, owner, 1, m1);
        }
        await using var db = CreateContext();
        var work = (await db.Database.SqlQueryRaw<TaxiDriverHomeReader.TaxiWorkRow>(TaxiDriverHomeReader.WorkSql,
            new Npgsql.NpgsqlParameter("listing", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = owner.ListingId },
            new Npgsql.NpgsqlParameter("wantSuggestion", NpgsqlTypes.NpgsqlDbType.Boolean) { Value = false },
            new Npgsql.NpgsqlParameter("m1", NpgsqlTypes.NpgsqlDbType.Date) { Value = m1 },
            new Npgsql.NpgsqlParameter("m2", NpgsqlTypes.NpgsqlDbType.Date) { Value = m2 },
            new Npgsql.NpgsqlParameter("today", NpgsqlTypes.NpgsqlDbType.Date) { Value = TodayIst }).ToListAsync()).Single();

        var kit = new Be2Kit(db, paymentOn: false);
        var month = await new OwnerCalendarReader(db, kit.Contexts, kit.Festivals).MonthAsync(Ctx(owner), m1, last);
        var tile = month.Value!.Entries.Count(e => e.Kind == CalendarEntryKinds.Auto && !e.Cancelled && e.DateFrom >= m1 && e.DateFrom < m2);

        Assert.Equal(2, work.MonthBookings);
        Assert.Equal(tile, work.MonthBookings);
    }
}
