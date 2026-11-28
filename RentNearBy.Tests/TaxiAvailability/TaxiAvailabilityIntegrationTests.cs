using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RentNearBy.Core.DTOs.Owner;
using RentNearBy.Core.Entities.Owner;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Repositories;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Infrastructure.Services.Owner;
using RentNearBy.Infrastructure.Services.Owner.Be1;
using RentNearBy.Infrastructure.Services.Retention;
using RentNearBy.Infrastructure.Services.Retention.Owner;
using RentNearBy.Infrastructure.Services.TaxiRequests;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Owner.Be1;
using RentNearBy.Tests.Support.Buddy;
using RentNearBy.Tests.Support.Owner;
using RentNearBy.Tests.Trips;

namespace RentNearBy.Tests.TaxiAvailability;

// PLAN_BE section 6 items 14-27 and contract 1.8 races. Needs RENTNEARBY_TEST_DB (PostGIS) and the regenerated InitialCreate
// (TaxiDayOffs, PopularRoutes); WRITTEN, NOT RUN on this machine.
public class TaxiAvailabilityIntegrationTests(TestDatabase db) : IntegrationTestBase(db)
{
    private static DateOnly TodayIst => IstTime.TodayIst(DateTime.UtcNow);

    private static OwnerContext Ctx(OwnerSeed.OwnerRow owner, PlanLimits? limits = null)
        => new(owner.UserId, owner.Kind, owner.ListingId, "Approved", null, null, true, null, 0, null, 0,
            limits ?? Be1Kit.Free(ListingKinds.Taxi), TodayIst, [0, 0, 0, null]);

    private static TaxiDayOffService Service(ApplicationDbContext c) => new(c, NullLogger<TaxiDayOffService>.Instance);

    private static TaxiDayOffChanges Changes(IEnumerable<(DateOnly Date, string? Reason)>? set = null, IEnumerable<DateOnly>? clear = null)
        => TaxiDayOffRules.Parse(new TaxiDaysOffSaveRequest
        {
            Set = set?.Select(s => new TaxiDayOffSetItem { Date = TaxiSearchDate.Text(s.Date), Reason = s.Reason }).ToList(),
            Clear = clear?.Select(d => (string?)TaxiSearchDate.Text(d)).ToList(),
        }).Changes!;

    // A day inside this month's window that is at least today.
    private static DateOnly InMonth(int offset = 0)
    {
        var last = new DateOnly(TodayIst.Year, TodayIst.Month, 1).AddMonths(1).AddDays(-1);
        var d = TodayIst.AddDays(offset);
        return d > last ? last : d;
    }

    private async Task<List<TaxiDayOff>> RowsAsync(Guid listingId)
    {
        await using var c = CreateContext();
        return await c.Set<TaxiDayOff>().AsNoTracking().Where(x => x.TaxiListingId == listingId).OrderBy(x => x.Date).ToListAsync();
    }

    private async Task<OwnerSeed.OwnerRow> DriverAsync()
    {
        await using var c = CreateContext();
        return await OwnerSeed.TaxiDriverAsync(c);
    }

    [IntegrationFact]
    public async Task Save_sets_and_clears_replays_the_same_state_and_edits_a_reason()
    {
        var owner = await DriverAsync();
        var d = InMonth();
        await using (var c = CreateContext())
        {
            var first = await Service(c).SaveAsync(Ctx(owner), Changes([(d, "Family function")]));
            Assert.True(first.Ok);
            Assert.Equal("Family function", Assert.Single(first.Value!.DaysOff).Reason);
            var again = await Service(c).SaveAsync(Ctx(owner), Changes([(d, "Family function")]));
            Assert.Equal(first.Value.DaysOff, again.Value!.DaysOff);
            var edit = await Service(c).SaveAsync(Ctx(owner), Changes([(d, null)]));
            Assert.Null(Assert.Single(edit.Value!.DaysOff).Reason);
            var cleared = await Service(c).SaveAsync(Ctx(owner), Changes(clear: [d]));
            Assert.Empty(cleared.Value!.DaysOff);
        }
        Assert.Empty(await RowsAsync(owner.ListingId));
    }

    [IntegrationFact]
    public async Task One_booked_day_refuses_the_whole_save_and_a_cancelled_booking_frees_it()
    {
        var owner = await DriverAsync();
        var booked = InMonth();
        CalendarEntry auto;
        await using (var c = CreateContext())
        {
            auto = await OwnerSeed.AutoEntryAsync(c, owner, CalendarSourceTypes.TaxiBooking, Guid.NewGuid(), booked, booked);
            c.Set<TaxiDayOff>().Add(new TaxiDayOff { TaxiListingId = owner.ListingId, Date = booked.AddDays(400), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            await c.SaveChangesAsync();
        }
        await using (var c = CreateContext())
        {
            // An already-off day (reason update) plus the booked day: the booked day refuses the whole call, nothing is written.
            var refused = await Service(c).SaveAsync(Ctx(owner), Changes([(booked.AddDays(400), "kept"), (booked, null)]));
            Assert.Equal(OwnerErrorTypes.DayOffBooked, refused.Error!.Type);
            Assert.Null(Assert.Single(await RowsAsync(owner.ListingId)).Reason);
        }
        await using (var c = CreateContext())
            await c.Set<CalendarEntry>().Where(x => x.Id == auto.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Cancelled, true));
        await using (var c = CreateContext())
            Assert.True((await Service(c).SaveAsync(Ctx(owner), Changes([(booked, null)]))).Ok);
    }

    [IntegrationFact]
    public async Task A_new_day_beyond_the_free_window_is_refused_but_a_reason_update_there_passes()
    {
        var owner = await DriverAsync();
        var nextMonth = new DateOnly(TodayIst.Year, TodayIst.Month, 1).AddMonths(1).AddDays(2);
        await using (var c = CreateContext())
        {
            var refused = await Service(c).SaveAsync(Ctx(owner), Changes([(nextMonth, null)]));
            Assert.Equal(OwnerErrorTypes.PlanFeatureLocked, refused.Error!.Type);
            var off = await Service(c).SaveAsync(Ctx(owner, Be1Kit.Off(ListingKinds.Taxi)), Changes([(nextMonth.AddMonths(12), null)]));
            Assert.Equal(OwnerErrorTypes.CalendarOutsideWindow, off.Error!.Type);
        }
        await using (var c = CreateContext())
        {
            c.Set<TaxiDayOff>().Add(new TaxiDayOff { TaxiListingId = owner.ListingId, Date = nextMonth, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            await c.SaveChangesAsync();
        }
        await using (var c = CreateContext())
        {
            var update = await Service(c).SaveAsync(Ctx(owner), Changes([(nextMonth, "Wedding")]));
            Assert.True(update.Ok);
            Assert.Contains(update.Value!.DaysOff, x => x.Date == nextMonth && x.Reason == "Wedding");
        }
    }

    [IntegrationFact]
    public async Task Driver_b_never_changes_driver_a_rows()
    {
        var a = await DriverAsync();
        var b = await DriverAsync();
        var d = InMonth();
        await using (var c = CreateContext()) await Service(c).SaveAsync(Ctx(a), Changes([(d, "A")]));
        await using (var c = CreateContext()) await Service(c).SaveAsync(Ctx(b), Changes([(d, "B")], []));
        await using (var c = CreateContext()) await Service(c).SaveAsync(Ctx(b), Changes(clear: [d]));
        Assert.Equal("A", Assert.Single(await RowsAsync(a.ListingId)).Reason);
        Assert.Empty(await RowsAsync(b.ListingId));
    }

    [IntegrationFact]
    public async Task Two_parallel_saves_of_one_driver_end_in_one_of_the_two_orders()
    {
        var owner = await DriverAsync();
        var d = InMonth();
        await using var c1 = CreateContext();
        await using var c2 = CreateContext();
        var results = await Task.WhenAll(
            Service(c1).SaveAsync(Ctx(owner), Changes([(d, "first")])),
            Service(c2).SaveAsync(Ctx(owner), Changes([(d, "second")])));
        Assert.All(results, r => Assert.True(r.Ok));
        var row = Assert.Single(await RowsAsync(owner.ListingId));
        Assert.Contains(row.Reason, new[] { "first", "second" });
    }

    // The confirm protocol (DriverCalendar: taxi row lock, overlap query, then the booking's Auto row) against a save of the same day.
    [IntegrationFact]
    public async Task Save_marking_a_day_and_a_confirm_covering_it_never_both_win()
    {
        var owner = await DriverAsync();
        var d = InMonth();
        async Task<bool> ConfirmAsync()
        {
            await using var c = CreateContext();
            await using var tx = await DriverCalendar.BeginConfirmTransactionAsync(c);
            var check = await DriverCalendar.LockAndCheckAsync(new EfDriverCalendarSql(c), owner.ListingId, new TaxiDateRange(d, d), null, default);
            if (check.Outcome != DriverCheckOutcome.Free) return false;
            await OwnerSeed.AutoEntryAsync(c, owner, CalendarSourceTypes.TaxiBooking, Guid.NewGuid(), d, d);
            await tx.CommitAsync();
            return true;
        }
        await using var sc = CreateContext();
        var save = Service(sc).SaveAsync(Ctx(owner), Changes([(d, null)]));
        var confirm = ConfirmAsync();
        await Task.WhenAll(save, confirm);
        Assert.True(save.Result.Ok ^ confirm.Result, "exactly one may win");
        if (!save.Result.Ok) Assert.Equal(OwnerErrorTypes.DayOffBooked, save.Result.Error!.Type);
    }

    // Contract 1.8 / R16: a day off does not close a ring at accept or select; confirm and its Selected banner refuse it.
    [IntegrationFact]
    public async Task A_day_off_is_a_clash_for_confirm_and_the_banner_only_never_for_accept_or_select()
    {
        var owner = await DriverAsync();
        var d = InMonth();
        await using (var c = CreateContext())
        {
            c.Set<TaxiDayOff>().Add(new TaxiDayOff { TaxiListingId = owner.ListingId, Date = d, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            await c.SaveChangesAsync();
        }
        await using (var c = CreateContext())
        {
            Assert.Null(await DriverCalendar.ConflictForAsync(c, owner.ListingId, d, d));
            Assert.Equal(new TaxiDateRange(d, d), await DriverCalendar.SelectedConflictForAsync(c, owner.ListingId, d, d));
            await using var tx = await DriverCalendar.BeginConfirmTransactionAsync(c);
            var check = await DriverCalendar.LockAndCheckAsync(new EfDriverCalendarSql(c), owner.ListingId, new TaxiDateRange(d, d), null, default);
            Assert.Equal(DriverCheckOutcome.Overlap, check.Outcome);
            Assert.Equal(new TaxiDateRange(d, d), check.Clash);
        }
    }

    // I-25: both orders end with a booked day and no day off: the create clears a day off saved first, a save after the create is refused.
    [IntegrationFact]
    public async Task Save_and_a_manual_create_on_the_same_day_follow_the_lock_order()
    {
        var owner = await DriverAsync();
        var d = InMonth();
        await using var c1 = CreateContext();
        await using var c2 = CreateContext();
        var save = Service(c1).SaveAsync(Ctx(owner), Changes([(d, null)]));
        var create = new OwnerCalendarWriter(c2, NullLogger<OwnerCalendarWriter>.Instance)
            .CreateAsync(Ctx(owner), Guid.NewGuid(), new ManualEntryInput("Phone booking", d, d, null, null, null, null, default));
        await Task.WhenAll(save, create);
        Assert.True(create.Result.Ok);
        if (!save.Result.Ok) Assert.Equal(OwnerErrorTypes.DayOffBooked, save.Result.Error!.Type);
        Assert.Empty(await RowsAsync(owner.ListingId));
    }

    // I-25: a phone booking on a day off is allowed and turns the day into a booked day; an unchanged edit passes.
    [IntegrationFact]
    public async Task Calendar_create_on_an_off_day_clears_the_day_off_and_an_unchanged_edit_passes()
    {
        var owner = await DriverAsync();
        var d = InMonth();
        var other = InMonth(1) == d ? d : InMonth(1);
        CalendarEntry trip;
        await using (var c = CreateContext())
            trip = (await OwnerSeed.ManualEntriesAsync(c, owner, 1, other))[0];
        await using (var c = CreateContext())
        {
            c.Set<TaxiDayOff>().Add(new TaxiDayOff { TaxiListingId = owner.ListingId, Date = d, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            await c.SaveChangesAsync();
        }
        await using (var c = CreateContext())
        {
            var writer = new OwnerCalendarWriter(c, NullLogger<OwnerCalendarWriter>.Instance);
            var created = await writer.CreateAsync(Ctx(owner), Guid.NewGuid(), new ManualEntryInput("Trip", d, d, null, null, null, null, default));
            Assert.True(created.Ok);
            Assert.Empty(await RowsAsync(owner.ListingId));
            var renamed = await writer.UpdateAsync(Ctx(owner), trip.Id, trip.Version, new ManualEntryInput("Renamed", other, other, null, null, null, null, default));
            Assert.True(renamed.Ok);
            Assert.Null(await writer.DeleteAsync(Ctx(owner), trip.Id));
        }
    }

    [IntegrationFact]
    public async Task The_feed_hides_a_taxi_with_a_day_off_or_a_booking_on_d_and_shows_it_on_d_plus_one()
    {
        var off = await DriverAsync();
        var booked = await DriverAsync();
        var manual = await DriverAsync();
        var hiding = await DriverAsync();
        var d = TodayIst.AddDays(2);
        // Own random pin, so the shared test database's other taxis never fill the clamped page.
        var lat = Math.Round(10 + Random.Shared.NextDouble() * 5, 4);
        var lng = Math.Round(75 + Random.Shared.NextDouble() * 5, 4);
        var ids = new[] { off.ListingId, booked.ListingId, manual.ListingId, hiding.ListingId };
        await using (var c = CreateContext())
        {
            await c.TaxiListings.Where(t => ids.Contains(t.Id)).ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Latitude, (decimal?)lat)
                .SetProperty(t => t.Longitude, (decimal?)lng));
            // Featured: one free taxi and the off taxi carry a rank; only the free one may come back on D.
            await c.TaxiListings.Where(t => t.Id == manual.ListingId || t.Id == off.ListingId)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.PriorityRank, (short)1));
            c.Set<TaxiDayOff>().Add(new TaxiDayOff { TaxiListingId = off.ListingId, Date = d, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            await c.SaveChangesAsync();
            await OwnerSeed.AutoEntryAsync(c, booked, CalendarSourceTypes.TaxiBooking, Guid.NewGuid(), d, d);
            // I-25: this phone booking keeps taking app bookings (KeepVisible); a plain one hides the taxi like an app booking.
            await OwnerSeed.ManualEntriesAsync(c, manual, 1, d, keepVisible: true);
            await OwnerSeed.ManualEntriesAsync(c, hiding, 1, d);
        }
        await using (var c = CreateContext())
        {
            var q = new NearbyFeedQuery(c);
            var onD = (await q.PageAsync(NearbyKind.Taxi, lat, lng, null, 500, new NearbyFilter(Day: d))).Select(r => r.Id).ToHashSet();
            var nextDay = (await q.PageAsync(NearbyKind.Taxi, lat, lng, null, 500, new NearbyFilter(Day: d.AddDays(1)))).Select(r => r.Id).ToHashSet();
            Assert.DoesNotContain(off.ListingId, onD);
            Assert.DoesNotContain(booked.ListingId, onD);
            Assert.Contains(manual.ListingId, onD);
            Assert.DoesNotContain(hiding.ListingId, onD);
            Assert.Contains(hiding.ListingId, nextDay);
            Assert.Contains(off.ListingId, nextDay);
            Assert.Contains(booked.ListingId, nextDay);
            var featured = await q.FeaturedAsync(NearbyKind.Taxi, lat, lng, 1, 50, new NearbyFilter(Day: d));
            Assert.Contains(featured, r => r.Id == manual.ListingId);
            Assert.DoesNotContain(featured, r => r.Id == off.ListingId);
            var featuredNextDay = await q.FeaturedAsync(NearbyKind.Taxi, lat, lng, 1, 50, new NearbyFilter(Day: d.AddDays(1)));
            Assert.Contains(featuredNextDay, r => r.Id == off.ListingId);
        }
    }

    [IntegrationFact]
    public async Task Popular_counts_both_directions_once_per_taxi_skips_hidden_and_off_taxis_and_hides_zero_routes()
    {
        var a = await DriverAsync();
        var b = await DriverAsync();
        // Own random city keys, so routes of earlier runs and other tests never count on this pair.
        var h = 900_000_000L + Random.Shared.Next(1, 90_000_000);
        var n = h + 1;
        var nowhere = h + 2;
        var pairId = Guid.NewGuid();
        var emptyId = Guid.NewGuid();
        var today = TodayIst;
        await using (var c = CreateContext())
        {
            var now = DateTime.UtcNow;
            c.TaxiRoutes.AddRange(
                Route(a.ListingId, 0, h, n, 1500), Route(a.ListingId, 1, n, h, 1200),
                Route(b.ListingId, 0, h, n, 900, hidden: true));
            c.Set<PopularRoute>().AddRange(
                new PopularRoute { Id = pairId, FromCityKey = h, ToCityKey = n, FromCityName = "From test", ToCityName = "To test", SortOrder = 10_000, IsActive = true, CreatedAt = now, UpdatedAt = now },
                new PopularRoute { Id = emptyId, FromCityKey = n, ToCityKey = nowhere, FromCityName = "To test", ToCityName = "Nowhere", SortOrder = 10_001, IsActive = true, CreatedAt = now, UpdatedAt = now });
            await c.SaveChangesAsync();
        }
        await using (var c = CreateContext())
        {
            var rows = await c.Database.SqlQueryRaw<PopularRouteReader.Row>(PopularRouteReader.Sql, TaxiAvailabilitySql.Day(today)).ToListAsync();
            var pair = Assert.Single(rows, r => r.Id == pairId);
            Assert.Equal(1, pair.Taxis);
            Assert.Equal(1200, pair.MinFare);
            Assert.DoesNotContain(rows, r => r.Id == emptyId);
            c.Set<TaxiDayOff>().Add(new TaxiDayOff { TaxiListingId = a.ListingId, Date = today, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            await c.SaveChangesAsync();
            var afterOff = await c.Database.SqlQueryRaw<PopularRouteReader.Row>(PopularRouteReader.Sql, TaxiAvailabilitySql.Day(today)).ToListAsync();
            Assert.DoesNotContain(afterOff, r => r.Id == pairId || r.Id == emptyId);
        }
    }

    private static TaxiRoute Route(Guid taxi, short index, long from, long to, int fare, bool hidden = false) => new()
    {
        Id = Guid.NewGuid(), TaxiListingId = taxi, RouteIndex = hidden ? (short)(101 + index) : index, FromName = "A", ToName = "B",
        FromLatitude = 29.2183m, FromLongitude = 79.5130m, ToLatitude = 29.3919m, ToLongitude = 79.4542m,
        FromCityKey = from, ToCityKey = to, Fare = fare, TripType = TaxiTripTypes.OneWay, HiddenByPlan = hidden,
        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
    };

    [IntegrationFact]
    public async Task Admin_store_refuses_duplicates_reorders_and_stays_under_the_cap()
    {
        await using (var c = CreateContext()) await c.Database.ExecuteSqlRawAsync("DELETE FROM \"PopularRoutes\"");
        var resolver = CityFixture.Resolver(CityFixture.Haldwani, CityFixture.Nainital, CityFixture.Kathgodam);
        await using var c1 = CreateContext();
        var store = new PopularRouteAdminStore(c1, resolver);
        var first = await store.CreateAsync(CityFixture.Haldwani.CityKey, CityFixture.Nainital.CityKey);
        Assert.True(first.Ok);
        Assert.Equal("Haldwani", first.Value!.FromCityName);
        await using (var c2 = CreateContext())
            Assert.Equal(OwnerErrorTypes.PopularRouteDuplicate, (await new PopularRouteAdminStore(c2, resolver).CreateAsync(CityFixture.Haldwani.CityKey, CityFixture.Nainital.CityKey)).Error!.Type);
        Assert.Equal(OwnerErrorTypes.PopularRouteCityUnknown, (await store.CreateAsync(CityFixture.Haldwani.CityKey, 999)).Error!.Type);
        var second = await store.CreateAsync(CityFixture.Nainital.CityKey, CityFixture.Haldwani.CityKey);
        Assert.True(second.Ok);

        var reordered = await store.ReorderAsync([second.Value!.Id, first.Value.Id]);
        Assert.Equal(new[] { second.Value.Id, first.Value.Id }, reordered.Value!.Select(x => x.Id).ToArray());
        Assert.Equal(OwnerErrorTypes.PopularRouteOrderStale, (await store.ReorderAsync([first.Value.Id])).Error!.Type);
        Assert.False((await store.SetActiveAsync(first.Value.Id, false))!.IsActive);
        Assert.Null(await store.SetActiveAsync(Guid.NewGuid(), false));
        await store.DeleteAsync(first.Value.Id);
        await store.DeleteAsync(first.Value.Id);
        Assert.Single(await store.ListAsync());
    }

    [IntegrationFact]
    public async Task Two_parallel_creates_of_one_pair_give_one_row()
    {
        await using (var c = CreateContext()) await c.Database.ExecuteSqlRawAsync("DELETE FROM \"PopularRoutes\"");
        var resolver = CityFixture.Resolver(CityFixture.Haldwani, CityFixture.Nainital);
        await using var c1 = CreateContext();
        await using var c2 = CreateContext();
        var results = await Task.WhenAll(
            new PopularRouteAdminStore(c1, resolver).CreateAsync(CityFixture.Haldwani.CityKey, CityFixture.Nainital.CityKey),
            new PopularRouteAdminStore(c2, resolver).CreateAsync(CityFixture.Haldwani.CityKey, CityFixture.Nainital.CityKey));
        Assert.Equal(1, results.Count(r => r.Ok));
        Assert.Equal(OwnerErrorTypes.PopularRouteDuplicate, results.Single(r => !r.Ok).Error!.Type);
    }

    [IntegrationFact]
    public async Task Retention_deletes_days_before_yesterday_and_purge_deletes_the_taxi_days()
    {
        var owner = await DriverAsync();
        await using (var c = CreateContext())
        {
            foreach (var off in new[] { -3, -2, -1, 0 })
                c.Set<TaxiDayOff>().Add(new TaxiDayOff { TaxiListingId = owner.ListingId, Date = TodayIst.AddDays(off), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            await c.SaveChangesAsync();
        }
        var rule = new DeclarativeRetentionRule(OwnerRetentionRules.Definitions.Single(d => d.Name == "TaxiDayOffs"));
        await using (var c = CreateContext())
        {
            var now = DateTime.UtcNow;
            var ctx = new RetentionContext(c, now, now.AddDays(-1), TodayIst, TodayIst.AddDays(-OwnerLimits.TaxiDayOffKeepDays), 1);
            while (await rule.DeleteBatchAsync(ctx, 1, CancellationToken.None) > 0) { }
        }
        Assert.Equal(new[] { TodayIst.AddDays(-1), TodayIst }, (await RowsAsync(owner.ListingId)).Select(r => r.Date).ToArray());

        await using (var c = CreateContext())
        {
            await using var tx = await c.Database.BeginTransactionAsync();
            await new OwnerListingPurge(c).PurgeAsync(ListingKinds.Taxi, [owner.ListingId]);
            await tx.CommitAsync();
        }
        Assert.Empty(await RowsAsync(owner.ListingId));
    }

    [IntegrationTheory]
    [InlineData(3)]
    [InlineData(30)]
    public async Task Read_is_one_statement_and_save_is_four_whatever_the_number_of_days(int days)
    {
        var owner = await DriverAsync();
        var start = new DateOnly(TodayIst.Year, TodayIst.Month, 1).AddMonths(1);
        var plus = Be1Kit.Plus(ListingKinds.Taxi);
        var set = Enumerable.Range(0, days).Select(i => (start.AddDays(i), (string?)"x")).ToList();
        await using var c = Db.CreateCountingContext(out var counter);
        var saved = await Service(c).SaveAsync(Ctx(owner, plus), Changes(set));
        Assert.True(saved.Ok);
        Assert.Equal(4, counter.Count);
        counter.Reset();
        await Service(c).ReadAsync(Ctx(owner, plus));
        Assert.Equal(1, counter.Count);
    }

    [IntegrationTheory]
    [InlineData(3)]
    [InlineData(30)]
    public async Task Popular_aggregate_is_one_statement_for_3_and_30_routes(int routes)
    {
        await using (var c = CreateContext())
        {
            await c.Database.ExecuteSqlRawAsync("DELETE FROM \"PopularRoutes\"");
            var now = DateTime.UtcNow;
            c.Set<PopularRoute>().AddRange(Enumerable.Range(0, routes).Select(i => new PopularRoute
            {
                Id = Guid.NewGuid(), FromCityKey = 1000 + i, ToCityKey = 2000 + i, FromCityName = "F", ToCityName = "T", SortOrder = i, IsActive = true,
                CreatedAt = now, UpdatedAt = now,
            }));
            await c.SaveChangesAsync();
        }
        await using var counted = Db.CreateCountingContext(out var counter);
        var reader = new PopularRouteReader(counted, CityFixture.Resolver(CityFixture.Haldwani), TimeProvider.System);
        await reader.ReadAsync();
        Assert.Equal(1, counter.Count);
    }
}
