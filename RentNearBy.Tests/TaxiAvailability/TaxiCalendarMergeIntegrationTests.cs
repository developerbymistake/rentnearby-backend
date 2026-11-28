using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RentNearBy.Core.DTOs.Owner;
using RentNearBy.Core.Entities.Owner;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.Owner;
using RentNearBy.Infrastructure.Services.Owner.Be1;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Owner.Be1;
using RentNearBy.Tests.Support.Owner;
using TaxiAvail = RentNearBy.Infrastructure.Services.TaxiAvailability;

namespace RentNearBy.Tests.TaxiAvailability;

// I-25 shared contract items 1-4 against PostGIS. Needs RENTNEARBY_TEST_DB and the regenerated InitialCreate (KeepVisible);
// WRITTEN, NOT RUN on this machine.
public class TaxiCalendarMergeIntegrationTests(TestDatabase db) : IntegrationTestBase(db)
{
    private static DateOnly TodayIst => IstTime.TodayIst(DateTime.UtcNow);

    private static OwnerContext Ctx(OwnerSeed.OwnerRow owner)
        => new(owner.UserId, owner.Kind, owner.ListingId, "Approved", null, null, true, null, 0, null, 0,
            Be1Kit.Free(owner.Kind), TodayIst, [0, 0, 0, null]);

    private static OwnerCalendarWriter Writer(ApplicationDbContext c) => new(c, NullLogger<OwnerCalendarWriter>.Instance);

    private static TaxiDayOffService Days(ApplicationDbContext c) => new(c, NullLogger<TaxiDayOffService>.Instance);

    private static ManualEntryInput Trip(DateOnly d, bool keepVisible = false, string label = "Phone booking")
        => new(label, d, d, null, null, null, null, default, keepVisible);

    // Days inside this month's free window, at least today.
    private static DateOnly InMonth(int offset = 0)
    {
        var last = new DateOnly(TodayIst.Year, TodayIst.Month, 1).AddMonths(1).AddDays(-1);
        var d = TodayIst.AddDays(offset);
        return d > last ? last : d;
    }

    private static TaxiDayOffChanges Set(DateOnly d) => TaxiDayOffRules.Parse(new TaxiDaysOffSaveRequest
    {
        Set = [new TaxiDayOffSetItem { Date = TaxiSearchDate.Text(d) }],
    }).Changes!;

    private async Task<OwnerSeed.OwnerRow> DriverAsync()
    {
        await using var c = CreateContext();
        return await OwnerSeed.TaxiDriverAsync(c);
    }

    private async Task DayOffAsync(Guid listingId, DateOnly d)
    {
        await using var c = CreateContext();
        c.Set<TaxiDayOff>().Add(new TaxiDayOff { TaxiListingId = listingId, Date = d, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await c.SaveChangesAsync();
    }

    private async Task<List<DateOnly>> OffDaysAsync(Guid listingId)
    {
        await using var c = CreateContext();
        return await c.Set<TaxiDayOff>().AsNoTracking().Where(x => x.TaxiListingId == listingId).Select(x => x.Date).OrderBy(x => x).ToListAsync();
    }

    private async Task<bool> FreeOnAsync(Guid listingId, DateOnly d)
    {
        await using var c = CreateContext();
        return await c.TaxiListings.Where(t => t.Id == listingId).Where(TaxiAvail.FreeOn(c, d)).AnyAsync();
    }

    [IntegrationFact]
    public async Task A_phone_booking_hides_the_taxi_and_keep_visible_shows_it_in_both_rule_forms()
    {
        var hiding = await DriverAsync();
        var keep = await DriverAsync();
        var d = InMonth(1);
        await using (var c = CreateContext())
        {
            Assert.True((await Writer(c).CreateAsync(Ctx(hiding), Guid.NewGuid(), Trip(d))).Ok);
            var kept = await Writer(c).CreateAsync(Ctx(keep), Guid.NewGuid(), Trip(d, keepVisible: true));
            Assert.True(kept.Value!.Entry.KeepVisible);
        }
        Assert.False(await FreeOnAsync(hiding.ListingId, d));
        Assert.True(await FreeOnAsync(keep.ListingId, d));
        Assert.True(await FreeOnAsync(hiding.ListingId, d.AddDays(1)));

        await using (var c = CreateContext())
        {
            var busy = await RentNearBy.Api.Handlers.TaxiFeedHandlers.BusyAsync(c, [hiding.ListingId, keep.ListingId], d, CancellationToken.None);
            Assert.Equal(new[] { hiding.ListingId }, busy);
        }
    }

    [IntegrationFact]
    public async Task Create_on_a_day_off_clears_it_for_a_hiding_row_and_keeps_it_for_a_keep_visible_row()
    {
        var a = await DriverAsync();
        var b = await DriverAsync();
        var d = InMonth(1);
        await DayOffAsync(a.ListingId, d);
        await DayOffAsync(b.ListingId, d);
        await DayOffAsync(b.ListingId, d.AddDays(400));
        await using (var c = CreateContext())
        {
            Assert.True((await Writer(c).CreateAsync(Ctx(a), Guid.NewGuid(), Trip(d))).Ok);
            Assert.True((await Writer(c).CreateAsync(Ctx(b), Guid.NewGuid(), Trip(d, keepVisible: true))).Ok);
        }
        Assert.Empty(await OffDaysAsync(a.ListingId));
        Assert.False(await FreeOnAsync(a.ListingId, d));
        Assert.Equal(new[] { d, d.AddDays(400) }, await OffDaysAsync(b.ListingId));
        Assert.False(await FreeOnAsync(b.ListingId, d));
    }

    [IntegrationFact]
    public async Task Moving_a_trip_onto_a_day_off_clears_it_and_a_stale_edit_keeps_it()
    {
        var owner = await DriverAsync();
        var from = InMonth();
        var to = InMonth(1);
        if (from == to) return;
        CalendarEntry trip;
        await using (var c = CreateContext()) trip = (await OwnerSeed.ManualEntriesAsync(c, owner, 1, from))[0];
        await DayOffAsync(owner.ListingId, to);
        await using (var c = CreateContext())
        {
            var stale = await Writer(c).UpdateAsync(Ctx(owner), trip.Id, trip.Version + 5, Trip(to));
            Assert.Equal(OwnerErrorTypes.StaleEdit, stale.Error!.Type);
        }
        Assert.Equal(new[] { to }, await OffDaysAsync(owner.ListingId));
        await using (var c = CreateContext())
        {
            var moved = await Writer(c).UpdateAsync(Ctx(owner), trip.Id, trip.Version, Trip(to));
            Assert.True(moved.Ok);
            Assert.Equal(to, moved.Value!.Entry.DateFrom);
        }
        Assert.Empty(await OffDaysAsync(owner.ListingId));
    }

    [IntegrationFact]
    public async Task A_day_off_beside_a_keep_visible_trip_is_allowed_and_kept_when_the_trip_is_edited()
    {
        var owner = await DriverAsync();
        var d = InMonth(1);
        CalendarEntry trip;
        await using (var c = CreateContext()) trip = (await OwnerSeed.ManualEntriesAsync(c, owner, 1, d, keepVisible: true))[0];
        await using (var c = CreateContext()) Assert.True((await Days(c).SaveAsync(Ctx(owner), Set(d))).Ok);
        Assert.Equal(new[] { d }, await OffDaysAsync(owner.ListingId));

        // A replay writes nothing; a real edit that keeps KeepVisible keeps the day off too, so the taxi stays hidden on D.
        await using (var c = CreateContext())
            Assert.True((await Writer(c).UpdateAsync(Ctx(owner), trip.Id, trip.Version + 5, Trip(d, keepVisible: true, label: trip.Label))).Ok);
        Assert.Equal(new[] { d }, await OffDaysAsync(owner.ListingId));
        await using (var c = CreateContext())
            Assert.True((await Writer(c).UpdateAsync(Ctx(owner), trip.Id, trip.Version, Trip(d, keepVisible: true, label: "Renamed"))).Ok);
        Assert.Equal(new[] { d }, await OffDaysAsync(owner.ListingId));
        Assert.False(await FreeOnAsync(owner.ListingId, d));
    }

    [IntegrationFact]
    public async Task Turning_keep_visible_off_on_an_edit_makes_the_day_booked_and_clears_the_day_off()
    {
        var owner = await DriverAsync();
        var d = InMonth(1);
        CalendarEntry trip;
        await using (var c = CreateContext()) trip = (await OwnerSeed.ManualEntriesAsync(c, owner, 1, d, keepVisible: true))[0];
        await using (var c = CreateContext()) Assert.True((await Days(c).SaveAsync(Ctx(owner), Set(d))).Ok);
        await using (var c = CreateContext())
        {
            var saved = await Writer(c).UpdateAsync(Ctx(owner), trip.Id, trip.Version, Trip(d, label: trip.Label));
            Assert.True(saved.Ok);
            Assert.False(saved.Value!.Entry.KeepVisible);
        }
        Assert.Empty(await OffDaysAsync(owner.ListingId));
        Assert.False(await FreeOnAsync(owner.ListingId, d));
    }

    [IntegrationFact]
    public async Task Confirm_overlap_sees_a_hiding_phone_booking_and_not_a_keep_visible_one()
    {
        var hiding = await DriverAsync();
        var keep = await DriverAsync();
        var d = InMonth(1);
        await using (var c = CreateContext())
        {
            await OwnerSeed.ManualEntriesAsync(c, hiding, 1, d);
            await OwnerSeed.ManualEntriesAsync(c, keep, 1, d, keepVisible: true);
        }
        await using (var c = CreateContext())
        {
            var clash = await RentNearBy.Infrastructure.Services.TaxiRequests.DriverCalendar.SelectedConflictForAsync(c, hiding.ListingId, d, d);
            Assert.Equal(new RentNearBy.Core.Models.TaxiRequests.TaxiDateRange(d, d), clash);
            Assert.Null(await RentNearBy.Infrastructure.Services.TaxiRequests.DriverCalendar.SelectedConflictForAsync(c, keep.ListingId, d, d));
            Assert.Null(await RentNearBy.Infrastructure.Services.TaxiRequests.DriverCalendar.ConflictForAsync(c, hiding.ListingId, d, d));
        }
    }

    [IntegrationFact]
    public async Task A_day_off_is_refused_over_a_hiding_trip_with_its_date_and_listed_as_booked()
    {
        var owner = await DriverAsync();
        var d = InMonth(1);
        await using (var c = CreateContext()) await OwnerSeed.ManualEntriesAsync(c, owner, 1, d);
        await using (var c = CreateContext())
        {
            var refused = await Days(c).SaveAsync(Ctx(owner), Set(d));
            Assert.Equal(OwnerErrorTypes.DayOffBooked, refused.Error!.Type);
            Assert.Contains(TaxiSearchDate.Text(d), System.Text.Json.JsonSerializer.Serialize(refused.Error.Data));
            var state = await Days(c).ReadAsync(Ctx(owner));
            Assert.Contains(d, state.BookedDays);
        }
        Assert.Empty(await OffDaysAsync(owner.ListingId));
    }

    [IntegrationFact]
    public async Task Deleting_the_trip_frees_the_day_for_a_day_off()
    {
        var owner = await DriverAsync();
        var d = InMonth(1);
        CalendarEntry trip;
        await using (var c = CreateContext()) trip = (await OwnerSeed.ManualEntriesAsync(c, owner, 1, d))[0];
        Assert.False(await FreeOnAsync(owner.ListingId, d));
        await using (var c = CreateContext()) Assert.Null(await Writer(c).DeleteAsync(Ctx(owner), trip.Id));
        Assert.True(await FreeOnAsync(owner.ListingId, d));
        await using (var c = CreateContext()) Assert.True((await Days(c).SaveAsync(Ctx(owner), Set(d))).Ok);
    }

    [IntegrationFact]
    public async Task A_stay_calendar_entry_never_stores_keep_visible_and_touches_no_day_off()
    {
        OwnerSeed.OwnerRow stay;
        var driver = await DriverAsync();
        var d = InMonth(1);
        await using (var c = CreateContext()) stay = await OwnerSeed.StayOwnerAsync(c);
        await DayOffAsync(driver.ListingId, d);
        await using (var c = CreateContext())
        {
            var input = new ManualEntryInput("Sharma family", TodayIst.AddDays(1), TodayIst.AddDays(2), null, null, null, null, default, KeepVisible: true);
            var created = await Writer(c).CreateAsync(Ctx(stay), Guid.NewGuid(), input);
            Assert.True(created.Ok);
            Assert.False(created.Value!.Entry.KeepVisible);
            var edited = await Writer(c).UpdateAsync(Ctx(stay), created.Value.Entry.Id, created.Value.Entry.Version, input with { Label = "Sharma" });
            Assert.True(edited.Ok);
            Assert.False(edited.Value!.Entry.KeepVisible);
        }
        Assert.Equal(new[] { d }, await OffDaysAsync(driver.ListingId));
    }
}
