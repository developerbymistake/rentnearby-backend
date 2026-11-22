using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RentNearBy.Core.Entities.Owner;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Services.Owner;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.Owner.Be1;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Owner.Be1;
using RentNearBy.Tests.Support.Owner;

namespace RentNearBy.Tests.Owner.Journeys;

// FB-04 / FB-05 / FB-55 (D-07, D-30; JT-B-ST-1 and JT-B-TX-1 step Ended): pure rules.
public class CalendarEditRulesTests
{
    private static readonly DateOnly Today = new(2026, 11, 25);

    private static OwnerCalendarWindowValue FreeWindow => OwnerCalendarWindow.Of(15, 0, true, Today);

    private static OwnerCalendarWindowValue PlusWindow => OwnerCalendarWindow.Of(OwnerLimits.PlusWindowDays, 6, false, Today);

    [Fact]
    public void Unchanged_dates_always_pass_even_outside_the_window()
    {
        // A long booking that started 10 days ago and ends past the Free window: only the note changes.
        var from = Today.AddDays(-10);
        var to = Today.AddDays(40);
        Assert.False(OwnerCalendarRules.InWindow(FreeWindow, from));
        Assert.False(OwnerCalendarRules.InWindow(FreeWindow, to));
        Assert.True(OwnerCalendarRules.DatesAllowed(FreeWindow, from, to, from, to));
    }

    [Fact]
    public void A_moved_date_must_be_inside_the_window()
    {
        var from = Today.AddDays(2);
        var to = Today.AddDays(4);
        Assert.True(OwnerCalendarRules.DatesAllowed(FreeWindow, from, to, from, Today.AddDays(5)));
        Assert.False(OwnerCalendarRules.DatesAllowed(FreeWindow, from, to, from, Today.AddDays(60)));
        Assert.True(OwnerCalendarRules.DatesAllowed(PlusWindow, from, to, from, Today.AddDays(60)));
        Assert.False(OwnerCalendarRules.DatesAllowed(FreeWindow, from, to, Today.AddDays(-30), to));
    }

    [Fact]
    public void Moved_dates_must_stay_in_order()
        => Assert.False(OwnerCalendarRules.DatesAllowed(PlusWindow, Today, Today.AddDays(2), Today.AddDays(5), Today.AddDays(3)));

    [Fact]
    public void Ended_means_the_stored_end_is_before_today()
    {
        Assert.True(OwnerCalendarRules.Ended(Today.AddDays(-1), Today));
        Assert.False(OwnerCalendarRules.Ended(Today, Today));
    }

    [Fact]
    public void Entry_ended_words_follow_the_role()
    {
        Assert.Equal(409, OwnerErrorTypes.StatusOf(OwnerErrorTypes.EntryEnded));
        Assert.Contains("booking has passed", OwnerErrorTypes.EntryEndedMessage(ListingKinds.Stay));
        Assert.Contains("trip has passed", OwnerErrorTypes.EntryEndedMessage(ListingKinds.Taxi));
    }

    [Fact]
    public void Free_manual_window_reaches_the_later_of_today_plus_15_and_month_end()
    {
        // 25 Nov: a booking on 3..5 Dec is allowed on Free (FB-05), so the hidden count bound is ManualTo, not MonthTo.
        Assert.True(FreeWindow.ManualTo >= Today.AddDays(15));
        Assert.True(OwnerCalendarRules.InsideWindow(FreeWindow, new DateOnly(2026, 12, 3), new DateOnly(2026, 12, 5)));
        Assert.True(FreeWindow.ManualTo > FreeWindow.MonthTo);
    }

    [Fact]
    public void Statements_carry_the_ended_window_visibility_and_ceiling_guards()
    {
        Assert.Contains("c.\"DateTo\" >= @today", OwnerCalendarSql.Update);
        Assert.Contains("(@fromOk OR c.\"DateFrom\" = @from) AND (@toOk OR c.\"DateTo\" = @to)", OwnerCalendarSql.Update);
        Assert.Contains(OwnerCalendarSql.Visible(), OwnerCalendarSql.Update);
        Assert.Contains(OwnerCalendarSql.Visible(), OwnerCalendarSql.Money);
        Assert.Contains(OwnerCalendarSql.Visible(), OwnerCalendarSql.OneVisible);
        Assert.Contains("@keptFrom", OwnerCalendarSql.CreateCheck);
        Assert.Contains("w.\"DateFrom\" > @manualTo", OwnerCalendarSql.Upcoming);
        Assert.Equal(500, OwnerLimits.ManualRowsCeiling);
    }

    [Fact]
    public void Writer_no_longer_refuses_an_edit_before_reading_the_stored_dates()
    {
        var src = File.ReadAllText(Path.Combine(Trips.CompletionCounterGuardTests.Root(),
            "RentNearBy.Infrastructure", "Services", "Owner", "Be1", "OwnerCalendarWriter.cs"));
        var update = src[src.IndexOf("public async Task<Be1Result<CalendarEntryWrite>> UpdateAsync", StringComparison.Ordinal)..];
        update = update[..update.IndexOf("public async Task<Be1Error?> DeleteAsync", StringComparison.Ordinal)];
        Assert.DoesNotContain("InsideWindow", update);
        Assert.Contains("OwnerErrorTypes.EntryEnded", update);
    }
}

// Integration (WRITTEN, NOT RUN: needs RENTNEARBY_TEST_DB with PostGIS).
public class CalendarEditJourneyIntegrationTests(TestDatabase db) : IntegrationTestBase(db)
{
    private static DateOnly TodayIst => IstTime.TodayIst(DateTime.UtcNow);

    private static OwnerContext Ctx(OwnerSeed.OwnerRow owner, PlanLimits limits)
        => new(owner.UserId, owner.Kind, owner.ListingId, "Approved", null, null, true, null, 0, null, 0, limits, TodayIst, [0, 0, 0, null]);

    private static OwnerCalendarWriter Writer(ApplicationDbContext c) => new(c, NullLogger<OwnerCalendarWriter>.Instance);

    private static ManualEntryInput Input(DateOnly from, DateOnly to, string label = "Phone booking")
        => new(label, from, to, null, null, null, null, default);

    private async Task<CalendarEntry> EntryAsync(OwnerSeed.OwnerRow owner, DateOnly from, DateOnly to)
    {
        await using var c = CreateContext();
        var e = (await OwnerSeed.ManualEntriesAsync(c, owner, 1, from))[0];
        await c.Set<CalendarEntry>().Where(x => x.Id == e.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.DateFrom, from).SetProperty(x => x.DateTo, to));
        e.DateFrom = from;
        e.DateTo = to;
        return e;
    }

    [IntegrationFact]
    public async Task Edit_of_an_ended_entry_is_refused_and_its_money_note_still_saves()
    {
        OwnerSeed.OwnerRow owner;
        await using (var c = CreateContext()) owner = await OwnerSeed.StayOwnerAsync(c);
        var ended = await EntryAsync(owner, TodayIst.AddDays(-9), TodayIst.AddDays(-7));
        var ctx = Ctx(owner, Be1Kit.Free(ListingKinds.Stay));
        await using var db = CreateContext();
        var moved = await Writer(db).UpdateAsync(ctx, ended.Id, 1, Input(TodayIst.AddDays(3), TodayIst.AddDays(5)));
        Assert.Equal(OwnerErrorTypes.EntryEnded, moved.Error!.Type);
        var money = await new MoneyNoteService(db).SetAsync(ctx, ended.Id, 1, new MoneyNote(5000, 1000, false));
        Assert.True(money.Ok);
    }

    [IntegrationFact]
    public async Task Edit_note_of_a_long_running_entry_passes_and_moving_its_end_outside_is_refused()
    {
        OwnerSeed.OwnerRow owner;
        await using (var c = CreateContext()) owner = await OwnerSeed.StayOwnerAsync(c);
        var e = await EntryAsync(owner, TodayIst.AddDays(-10), TodayIst.AddDays(40));
        var ctx = Ctx(owner, Be1Kit.Free(ListingKinds.Stay));
        await using var db = CreateContext();
        var note = await Writer(db).UpdateAsync(ctx, e.Id, 1, Input(e.DateFrom, e.DateTo, "Sharma family"));
        Assert.True(note.Ok);
        var outside = await Writer(db).UpdateAsync(ctx, e.Id, 2, Input(e.DateFrom, TodayIst.AddDays(80), "Sharma family"));
        Assert.Equal(OwnerErrorTypes.CalendarOutsideWindow, outside.Error!.Type);
    }

    [IntegrationFact]
    public async Task Hidden_entry_is_not_found_for_read_edit_and_money_but_delete_works()
    {
        OwnerSeed.OwnerRow owner;
        List<CalendarEntry> rows;
        await using (var c = CreateContext())
        {
            owner = await OwnerSeed.StayOwnerAsync(c);
            rows = await OwnerSeed.ManualEntriesAsync(c, owner, 6, TodayIst.AddDays(1), stepDays: 2);
        }
        var hidden = rows.OrderBy(r => r.DateTo).ThenBy(r => r.Id).Last();
        var ctx = Ctx(owner, Be1Kit.Free(ListingKinds.Stay));
        await using var db = CreateContext();
        Assert.Null(await OwnerCalendarReader.ReadVisibleAsync(db, ctx, hidden.Id, default));
        Assert.Equal(Be1ErrorTypes.NotFound, (await Writer(db).UpdateAsync(ctx, hidden.Id, 1, Input(hidden.DateFrom, hidden.DateTo, "x"))).Error!.Type);
        Assert.Equal(Be1ErrorTypes.NotFound, (await new MoneyNoteService(db).SetAsync(ctx, hidden.Id, 1, new MoneyNote(100, null, false))).Error!.Type);
        Assert.Null(await Writer(db).DeleteAsync(ctx, hidden.Id));
    }

    [IntegrationFact]
    public async Task Past_dated_creates_stop_at_the_ceiling()
    {
        OwnerSeed.OwnerRow owner;
        await using (var c = CreateContext())
        {
            owner = await OwnerSeed.StayOwnerAsync(c);
            await OwnerSeed.ManualEntriesAsync(c, owner, OwnerLimits.ManualRowsCeiling, TodayIst.AddDays(-80), stepDays: 0);
        }
        await using var db = CreateContext();
        var refused = await Writer(db).CreateAsync(Ctx(owner, Be1Kit.Plus(ListingKinds.Stay)), Guid.NewGuid(),
            Input(TodayIst.AddDays(-2), TodayIst.AddDays(-1)));
        Assert.Equal(OwnerErrorTypes.CalendarEntryLimit, refused.Error!.Type);
    }
}
