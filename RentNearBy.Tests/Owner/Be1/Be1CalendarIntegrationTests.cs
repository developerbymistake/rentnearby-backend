using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RentNearBy.Core.Entities.Owner;
using RentNearBy.Core.Entities.TaxiRequests;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.Owner;
using RentNearBy.Infrastructure.Services.Owner.Be1;
using RentNearBy.Infrastructure.Services.Retention;
using RentNearBy.Infrastructure.Services.Retention.Owner;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Support.Buddy;
using RentNearBy.Tests.Support.Owner;

namespace RentNearBy.Tests.Owner.Be1;

// Contract 11.2, 11.3, 11.4 for lane BE1. Needs RENTNEARBY_TEST_DB (PostGIS); WRITTEN, NOT RUN on this machine.
public class Be1CalendarIntegrationTests(TestDatabase db) : IntegrationTestBase(db)
{
    private static DateOnly TodayIst => IstTime.TodayIst(DateTime.UtcNow);

    private ApplicationDbContext HookedContext()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(Db.ConnectionString!, o => o.UseNetTopologySuite())
            .AddInterceptors(new OwnerCalendarSyncInterceptor(new OwnerCalendarSync()))
            .Options);

    private static OwnerContext Ctx(OwnerSeed.OwnerRow owner, PlanLimits limits)
        => new(owner.UserId, owner.Kind, owner.ListingId, "Approved", null, null, true, null, 0, null, 0, limits, TodayIst, [0, 0, 0, null]);

    private static OwnerCalendarWriter Writer(ApplicationDbContext c) => new(c, NullLogger<OwnerCalendarWriter>.Instance);

    private static OwnerCalendarReader Reader(ApplicationDbContext c) => new(c, null!, new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()));

    private static ManualEntryInput StayInput(int fromDays, int nights = 1, string label = "Phone booking")
        => new(label, TodayIst.AddDays(fromDays), TodayIst.AddDays(fromDays + nights), null, null, null, null, default);

    private async Task<TripEnquiry> EnquiryAsync(OwnerSeed.OwnerRow owner, Guid traveller, string status, int fromDays, int nights)
    {
        await using var c = CreateContext();
        var now = DateTime.UtcNow;
        var e = new TripEnquiry
        {
            Id = Guid.NewGuid(), TravellerUserId = traveller, OwnerUserId = owner.UserId, Kind = TripEnquiryKinds.Stay, ListingId = owner.ListingId,
            ListingName = "Lake View Stay", CheckIn = TodayIst.AddDays(fromDays), CheckOut = TodayIst.AddDays(fromDays + nights), Status = status,
            ExpiresAt = now.AddDays(2), AgreedTotal = 9000, ListedStayTotal = 9600, CreatedAt = now, UpdatedAt = now,
        };
        c.Set<TripEnquiry>().Add(e);
        await c.SaveChangesAsync();
        return e;
    }

    private async Task<List<CalendarEntry>> EntriesAsync(Guid listingId)
    {
        await using var c = CreateContext();
        return await c.Set<CalendarEntry>().AsNoTracking().Where(x => x.ListingId == listingId).OrderBy(x => x.DateFrom).ToListAsync();
    }

    [IntegrationFact]
    public async Task Create_on_free_refuses_the_fifth_active_entry_replays_the_same_id_and_refuses_another_owners_id()
    {
        OwnerSeed.OwnerRow owner, other;
        await using (var c = CreateContext())
        {
            owner = await OwnerSeed.StayOwnerAsync(c);
            other = await OwnerSeed.StayOwnerAsync(c);
            await OwnerSeed.ManualEntriesAsync(c, owner, 4, TodayIst.AddDays(1), stepDays: 2);
        }
        var free = Be1Kit.Free(ListingKinds.Stay);
        await using var ctx = CreateContext();
        var refused = await Writer(ctx).CreateAsync(Ctx(owner, free), Guid.NewGuid(), StayInput(10));
        Assert.Equal(OwnerErrorTypes.CalendarEntryLimit, refused.Error!.Type);
        var data = Assert.IsType<RentNearBy.Core.DTOs.Owner.PlanLimitRefusalDto>(refused.Error.Data);
        Assert.Equal((PlanFeatureKeys.CalendarEntries, 4, 4, true), (data.Key, data.Limit, data.Used, data.CanUpgrade));

        var id = Guid.NewGuid();
        var plus = Be1Kit.Plus(ListingKinds.Stay);
        var first = await Writer(ctx).CreateAsync(Ctx(owner, plus), id, StayInput(12));
        var replay = await Writer(ctx).CreateAsync(Ctx(owner, plus), id, StayInput(12));
        Assert.True(first.Ok && replay.Ok);
        Assert.Equal(first.Value!.Entry.Version, replay.Value!.Entry.Version);
        Assert.Equal(5, (await EntriesAsync(owner.ListingId)).Count);

        var taken = await Writer(ctx).CreateAsync(Ctx(other, plus), id, StayInput(12));
        Assert.Equal(OwnerErrorTypes.IdTaken, taken.Error!.Type);
    }

    [IntegrationFact]
    public async Task Two_phones_saving_the_fifth_entry_get_one_200_and_one_limit()
    {
        OwnerSeed.OwnerRow owner;
        await using (var c = CreateContext())
        {
            owner = await OwnerSeed.StayOwnerAsync(c);
            await OwnerSeed.ManualEntriesAsync(c, owner, 3, TodayIst.AddDays(1), stepDays: 2);
        }
        var free = Be1Kit.Free(ListingKinds.Stay);
        await using var a = CreateContext();
        await using var b = CreateContext();
        var results = await Task.WhenAll(
            Writer(a).CreateAsync(Ctx(owner, free), Guid.NewGuid(), StayInput(10)),
            Writer(b).CreateAsync(Ctx(owner, free), Guid.NewGuid(), StayInput(13)));
        Assert.Equal(1, results.Count(r => r.Ok));
        Assert.Equal(1, results.Count(r => r.Error?.Type == OwnerErrorTypes.CalendarEntryLimit));
    }

    [IntegrationFact]
    public async Task Edit_is_version_guarded_replays_and_auto_rows_cannot_be_edited_or_deleted()
    {
        OwnerSeed.OwnerRow owner;
        CalendarEntry manual, auto;
        await using (var c = CreateContext())
        {
            owner = await OwnerSeed.StayOwnerAsync(c);
            manual = (await OwnerSeed.ManualEntriesAsync(c, owner, 1, TodayIst.AddDays(2)))[0];
            auto = await OwnerSeed.AutoEntryAsync(c, owner, CalendarSourceTypes.StayEnquiry, Guid.NewGuid(), TodayIst.AddDays(5), TodayIst.AddDays(7));
        }
        var ctx = Ctx(owner, Be1Kit.Free(ListingKinds.Stay));
        await using var db = CreateContext();
        var input = StayInput(2, 2, "Sharma family");
        var saved = await Writer(db).UpdateAsync(ctx, manual.Id, 1, input);
        Assert.Equal(2, saved.Value!.Entry.Version);
        Assert.True((await Writer(db).UpdateAsync(ctx, manual.Id, 1, input)).Ok);
        var stale = await Writer(db).UpdateAsync(ctx, manual.Id, 1, input with { Label = "Other" });
        Assert.Equal(OwnerErrorTypes.StaleEdit, stale.Error!.Type);
        Assert.Equal(2, stale.Error.CurrentEntry!.Version);

        Assert.Equal(OwnerErrorTypes.EntryNotManual, (await Writer(db).UpdateAsync(ctx, auto.Id, 1, input)).Error!.Type);
        Assert.Equal(OwnerErrorTypes.EntryNotManual, (await Writer(db).DeleteAsync(ctx, auto.Id))!.Type);
        Assert.Null(await Writer(db).DeleteAsync(ctx, manual.Id));
        Assert.Null(await Writer(db).DeleteAsync(ctx, manual.Id));
        Assert.Equal(Be1ErrorTypes.NotFound, (await Writer(db).UpdateAsync(ctx, Guid.NewGuid(), 1, input)).Error!.Type);
    }

    [IntegrationFact]
    public async Task Money_notes_save_on_auto_rows_cancelled_too_and_a_stale_save_gets_the_stored_row()
    {
        OwnerSeed.OwnerRow owner;
        CalendarEntry auto;
        await using (var c = CreateContext())
        {
            owner = await OwnerSeed.TaxiDriverAsync(c);
            auto = await OwnerSeed.AutoEntryAsync(c, owner, CalendarSourceTypes.TaxiBooking, Guid.NewGuid(), TodayIst, TodayIst.AddDays(1), cancelled: true);
        }
        var ctx = Ctx(owner, Be1Kit.Free(ListingKinds.Taxi));
        await using var db = CreateContext();
        var service = new MoneyNoteService(db);
        var saved = await service.SetAsync(ctx, auto.Id, 1, new MoneyNote(8000, 2000, false));
        Assert.Equal(6000, MoneyNoteMath.Balance(saved.Value!.Money));
        Assert.True((await service.SetAsync(ctx, auto.Id, 1, new MoneyNote(8000, 2000, false))).Ok);
        Assert.Equal(OwnerErrorTypes.StaleEdit, (await service.SetAsync(ctx, auto.Id, 1, new MoneyNote(9000, 2000, false))).Error!.Type);
        Assert.Equal(OwnerErrorTypes.MoneyNoteInvalid, (await service.SetAsync(ctx, auto.Id, 2, new MoneyNote(1000, 2000, false))).Error!.Type);
    }

    [IntegrationFact]
    public async Task Free_month_shows_four_soonest_manual_rows_every_auto_row_and_the_hidden_count()
    {
        OwnerSeed.OwnerRow owner;
        var monthStart = new DateOnly(TodayIst.Year, TodayIst.Month, 1);
        await using (var c = CreateContext())
        {
            owner = await OwnerSeed.TaxiDriverAsync(c);
            await OwnerSeed.ManualEntriesAsync(c, owner, 12, TodayIst);
            await OwnerSeed.AutoEntryAsync(c, owner, CalendarSourceTypes.TaxiBooking, Guid.NewGuid(), TodayIst, TodayIst);
        }
        var ctx = Ctx(owner, Be1Kit.Free(ListingKinds.Taxi)) with { Extras = [12, 0, 0, null] };
        await using var db = CreateContext();
        var month = (await Reader(db).MonthAsync(ctx, monthStart, monthStart.AddMonths(1).AddDays(-1))).Value!;
        Assert.Single(month.Entries, e => e.Kind == CalendarEntryKinds.Auto);
        Assert.True(month.Entries.Count(e => e.Kind == CalendarEntryKinds.Manual) <= 4);
        Assert.True(month.HiddenCount >= 8);
    }

    [IntegrationFact]
    public async Task Block_refuses_overlap_asks_to_confirm_a_confirmed_booking_inside_and_saves_with_confirm()
    {
        OwnerSeed.OwnerRow owner;
        Guid traveller;
        await using (var c = CreateContext())
        {
            owner = await OwnerSeed.StayOwnerAsync(c);
            traveller = await OwnerSeed.UserAsync(c, UserRoles.SeekerId, "Neha");
        }
        await EnquiryAsync(owner, traveller, TripEnquiryStatuses.ConfirmedByBoth, 10, 2);
        var ctx = Ctx(owner, Be1Kit.Plus(ListingKinds.Stay));
        await using var db = CreateContext();
        var service = new BlockedRangeService(db, NullLogger<BlockedRangeService>.Instance);
        var asked = await service.CreateAsync(ctx, Guid.NewGuid(), TodayIst.AddDays(11), TodayIst.AddDays(12), "Family visit", confirm: false);
        Assert.Equal(OwnerErrorTypes.BlockedRangeNeedsConfirm, asked.Error!.Type);
        var id = Guid.NewGuid();
        var saved = await service.CreateAsync(ctx, id, TodayIst.AddDays(11), TodayIst.AddDays(12), "Family visit", confirm: true);
        Assert.Equal(1, saved.Value!.ConfirmedInside);
        Assert.True((await service.CreateAsync(ctx, id, TodayIst.AddDays(11), TodayIst.AddDays(12), "Family visit", confirm: true)).Ok);
        var overlap = await service.CreateAsync(ctx, Guid.NewGuid(), TodayIst.AddDays(12), TodayIst.AddDays(14), null, confirm: true);
        Assert.Equal(OwnerErrorTypes.BlockedRangeOverlap, overlap.Error!.Type);
        var free = await service.CreateAsync(Ctx(owner, Be1Kit.Free(ListingKinds.Stay)), Guid.NewGuid(), TodayIst.AddDays(30), TodayIst.AddDays(31), null, false);
        Assert.Equal(OwnerErrorTypes.BlockedRangeLimit, free.Error!.Type);
        Assert.True(await service.DeleteAsync(ctx, id));
        Assert.False(await service.DeleteAsync(ctx, id));
    }

    [IntegrationFact]
    public async Task Two_overlapping_blocks_saved_together_one_wins()
    {
        OwnerSeed.OwnerRow owner;
        await using (var c = CreateContext()) owner = await OwnerSeed.StayOwnerAsync(c);
        var ctx = Ctx(owner, Be1Kit.Plus(ListingKinds.Stay));
        await using var a = CreateContext();
        await using var b = CreateContext();
        var results = await Task.WhenAll(
            new BlockedRangeService(a, NullLogger<BlockedRangeService>.Instance).CreateAsync(ctx, Guid.NewGuid(), TodayIst.AddDays(3), TodayIst.AddDays(5), null, false),
            new BlockedRangeService(b, NullLogger<BlockedRangeService>.Instance).CreateAsync(ctx, Guid.NewGuid(), TodayIst.AddDays(4), TodayIst.AddDays(6), null, false));
        Assert.Equal(1, results.Count(r => r.Ok));
        Assert.Equal(1, results.Count(r => r.Error?.Type == OwnerErrorTypes.BlockedRangeOverlap));
    }

    [IntegrationFact]
    public async Task Accept_writes_one_auto_row_change_moves_it_cancel_keeps_money_notes()
    {
        OwnerSeed.OwnerRow owner;
        Guid traveller;
        await using (var c = CreateContext())
        {
            owner = await OwnerSeed.StayOwnerAsync(c);
            traveller = await OwnerSeed.UserAsync(c, UserRoles.SeekerId, "Neha Joshi");
        }
        var enquiry = await EnquiryAsync(owner, traveller, TripEnquiryStatuses.Offered, 10, 2);

        await using (var c = HookedContext())
        {
            var e = await c.Set<TripEnquiry>().SingleAsync(x => x.Id == enquiry.Id);
            e.Status = TripEnquiryStatuses.ConfirmedByBoth;
            await c.SaveChangesAsync();
            e.UpdatedAt = DateTime.UtcNow;
            await c.SaveChangesAsync();
        }
        var rows = await EntriesAsync(owner.ListingId);
        var row = Assert.Single(rows);
        Assert.Equal(("Neha Joshi", 9600, 9000), (row.Label, row.ListedTotal, row.AgreedTotal));

        await using (var c = CreateContext())
            await c.Set<CalendarEntry>().Where(x => x.Id == row.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.FinalAmount, 9000).SetProperty(x => x.AdvanceReceived, 3000));

        await using (var c = HookedContext())
        {
            var e = await c.Set<TripEnquiry>().SingleAsync(x => x.Id == enquiry.Id);
            e.CheckIn = TodayIst.AddDays(11);
            e.CheckOut = TodayIst.AddDays(14);
            await c.SaveChangesAsync();
            e.Status = TripEnquiryStatuses.CancelledByTraveller;
            e.CancelledAt = DateTime.UtcNow;
            await c.SaveChangesAsync();
        }
        row = Assert.Single(await EntriesAsync(owner.ListingId));
        Assert.Equal((TodayIst.AddDays(11), TodayIst.AddDays(14)), (row.DateFrom, row.DateTo));
        Assert.True(row.Cancelled);
        Assert.Equal(CalendarCancelledBy.Traveller, row.CancelledBy);
        Assert.Equal(TodayIst, row.CancelledOn);
        Assert.Equal((9000, 3000), (row.FinalAmount, row.AdvanceReceived));
    }

    [IntegrationFact]
    public async Task Taxi_booking_sync_writes_and_cancels_the_drivers_row_once()
    {
        OwnerSeed.OwnerRow driver;
        Guid traveller;
        await using (var c = CreateContext())
        {
            driver = await OwnerSeed.TaxiDriverAsync(c);
            traveller = await OwnerSeed.UserAsync(c, UserRoles.SeekerId, "Seema");
        }
        var booking = new TaxiBooking
        {
            Id = Guid.NewGuid(), RequestId = Guid.NewGuid(), OfferId = Guid.NewGuid(), TripId = Guid.NewGuid(), DriverListingId = driver.ListingId,
            DriverUserId = driver.UserId, TravellerUserId = traveller, DateFrom = TodayIst.AddDays(3), DateTo = TodayIst.AddDays(5),
            Status = BookingStates.Booked, PickupCityName = "Kathgodam", Seats = 4, TravellerName = "Seema", TripName = "Hills", BookedAt = DateTime.UtcNow,
        };
        await using (var c = CreateContext())
        {
            c.Set<TaxiBooking>().Add(booking);
            await c.SaveChangesAsync();
        }
        var sync = new OwnerCalendarSync();
        await using (var c = CreateContext())
        {
            await sync.SyncTaxiBookingAsync(c, booking.Id);
            await sync.SyncTaxiBookingAsync(c, booking.Id);
            await c.Set<TaxiBooking>().Where(b => b.Id == booking.Id).ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, BookingStates.Cancelled).SetProperty(b => b.CancelledBy, BookingCancelledBy.Driver).SetProperty(b => b.CancelledAt, DateTime.UtcNow));
            await sync.SyncTaxiBookingAsync(c, booking.Id);
        }
        var row = Assert.Single(await EntriesAsync(driver.ListingId));
        Assert.Equal(("Seema", "4 seats", "Kathgodam"), (row.Label, row.Detail, row.FromPlace));
        Assert.True(row.Cancelled);
        Assert.Equal(CalendarCancelledBy.Driver, row.CancelledBy);
        await using (var c = CreateContext())
            Assert.Empty(await sync.ManualDatesAsync(c, driver.ListingId, TodayIst, TodayIst.AddDays(10)));
    }

    [IntegrationFact]
    public async Task Purge_removes_entries_blocks_special_prices_and_stats_of_the_listing_only()
    {
        OwnerSeed.OwnerRow owner, other;
        await using (var c = CreateContext())
        {
            owner = await OwnerSeed.StayOwnerAsync(c);
            other = await OwnerSeed.StayOwnerAsync(c);
            foreach (var o in new[] { owner, other }) // batch-loop
            {
                await OwnerSeed.ManualEntriesAsync(c, o, 2, TodayIst.AddDays(1)); // batch-loop
                await OwnerSeed.BlocksAsync(c, o.ListingId, 1, TodayIst.AddDays(5)); // batch-loop
                await OwnerSeed.SpecialPricesAsync(c, o.ListingId, 1, TodayIst.AddDays(20)); // batch-loop
            }
            await c.Database.ExecuteSqlRawAsync(
                """INSERT INTO "OwnerWeeklyStats" ("ListingKind", "ListingId", "Day", "Shown", "RequestsNoRoute") VALUES ('Stay', {0}, {1}, 12, 0), ('Stay', {2}, {1}, 12, 0)""",
                owner.ListingId, TodayIst.AddDays(-1), other.ListingId);
        }
        await using (var c = CreateContext())
        {
            await using var tx = await c.Database.BeginTransactionAsync();
            await new OwnerListingPurge(c).PurgeAsync(ListingKinds.Stay, [owner.ListingId]);
            await tx.CommitAsync();
        }
        await using var check = CreateContext();
        Assert.False(await check.Set<CalendarEntry>().AnyAsync(x => x.ListingId == owner.ListingId));
        Assert.False(await check.Set<StayBlockedRange>().AnyAsync(x => x.StayListingId == owner.ListingId));
        Assert.False(await check.Set<StaySpecialPrice>().AnyAsync(x => x.StayListingId == owner.ListingId));
        Assert.False(await check.Set<OwnerWeeklyStat>().AnyAsync(x => x.ListingId == owner.ListingId));
        Assert.Equal(2, await check.Set<CalendarEntry>().CountAsync(x => x.ListingId == other.ListingId));
        Assert.True(await check.Set<StayBlockedRange>().AnyAsync(x => x.StayListingId == other.ListingId));
        Assert.True(await check.Set<OwnerWeeklyStat>().AnyAsync(x => x.ListingId == other.ListingId));
    }

    [IntegrationFact]
    public async Task Retention_deletes_entries_90_days_after_their_end_with_the_money_notes_and_keeps_newer()
    {
        OwnerSeed.OwnerRow owner;
        await using (var c = CreateContext())
        {
            owner = await OwnerSeed.TaxiDriverAsync(c);
            await OwnerSeed.ManualEntriesAsync(c, owner, 1, TodayIst.AddDays(-91), finalAmount: 5000, advance: 1000);
            await OwnerSeed.ManualEntriesAsync(c, owner, 1, TodayIst.AddDays(-89));
        }
        var rule = new DeclarativeRetentionRule(OwnerRetentionRules.Definitions.Single(d => d.Name == "OwnerCalendarEntries"));
        await using (var c = CreateContext())
        {
            var now = DateTime.UtcNow;
            var ctx = new RetentionContext(c, now, now.AddDays(-90), TodayIst, TodayIst.AddDays(-90), 90);
            while (await rule.DeleteBatchAsync(ctx, 1000, CancellationToken.None) > 0) { } // batch-loop
        }
        var left = Assert.Single(await EntriesAsync(owner.ListingId));
        Assert.Equal(TodayIst.AddDays(-89), left.DateFrom);
    }

    [IntegrationFact]
    public async Task Month_upcoming_past_summary_balance_and_blocks_have_fixed_query_counts()
    {
        OwnerSeed.OwnerRow owner = null!;
        var plus = Be1Kit.Plus(ListingKinds.Stay);
        Task Seed(ApplicationDbContext c, int n) => SeedAsync(c, n, o => owner = o);
        async Task SeedAsync(ApplicationDbContext c, int n, Action<OwnerSeed.OwnerRow> set)
        {
            var o = await OwnerSeed.StayOwnerAsync(c);
            set(o);
            await OwnerSeed.ManualEntriesAsync(c, o, n, TodayIst.AddDays(-20), finalAmount: 4000, advance: 1000);
            await OwnerSeed.BlocksAsync(c, o.ListingId, Math.Min(n, 10), TodayIst.AddDays(1));
        }
        var month = new DateOnly(TodayIst.Year, TodayIst.Month, 1);
        // Budgets exclude the owner context statement (contract 2.1: C1 stay 4, C2 2, C3 2, C9 2, C10 2, K1 2 with it).
        await QueryAssert.SameCountAsync(Db, Seed, c => Reader(c).MonthAsync(Ctx(owner, plus), month, month.AddDays(30)), budget: 3);
        await QueryAssert.SameCountAsync(Db, Seed, c => Reader(c).UpcomingAsync(Ctx(owner, plus), null), budget: 1);
        await QueryAssert.SameCountAsync(Db, Seed, c => Reader(c).PastAsync(Ctx(owner, plus), null), budget: 1);
        await QueryAssert.SameCountAsync(Db, Seed, c => Reader(c).SummaryAsync(Ctx(owner, plus), null), budget: 1);
        await QueryAssert.SameCountAsync(Db, Seed, c => Reader(c).BalanceDueAsync(Ctx(owner, plus), null, null), budget: 1);
        await QueryAssert.SameCountAsync(Db, Seed, c => new BlockedRangeService(c, NullLogger<BlockedRangeService>.Instance).ListAsync(Ctx(owner, plus)), budget: 1);
    }

    [IntegrationFact]
    public async Task Create_and_block_writes_have_fixed_query_counts()
    {
        OwnerSeed.OwnerRow owner = null!;
        var plus = Be1Kit.Plus(ListingKinds.Stay);
        async Task Seed(ApplicationDbContext c, int n)
        {
            owner = await OwnerSeed.StayOwnerAsync(c);
            await OwnerSeed.ManualEntriesAsync(c, owner, Math.Min(n, 40), TodayIst.AddDays(1));
        }
        // C5 <= 5 and K2 <= 4 with the context statement; BEGIN / COMMIT are not counted as statements of the method.
        await QueryAssert.SameCountAsync(Db, Seed, c => Writer(c).CreateAsync(Ctx(owner, plus), Guid.NewGuid(), StayInput(60)), budget: 6);
        await QueryAssert.SameCountAsync(Db, Seed,
            c => new BlockedRangeService(c, NullLogger<BlockedRangeService>.Instance).CreateAsync(Ctx(owner, plus), Guid.NewGuid(), TodayIst.AddDays(100), TodayIst.AddDays(101), null, false),
            budget: 5);
    }
}
