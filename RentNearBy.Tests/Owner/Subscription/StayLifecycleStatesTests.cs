using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Infrastructure.Services.Owner.Be1;
using RentNearBy.Infrastructure.Services.Owner.Be2;
using RentNearBy.Tests.Owner.Be1;
using RentNearBy.Tests.Support.Owner;

namespace RentNearBy.Tests.Owner.Subscription;

// Subscription matrix (Stay owner), the lifecycle states L5 to L14 and the access switches (calendar entries, blocked ranges,
// upcoming days, months ahead, month money summary, morning reminder). Which plan row is "entitled" in each state, what limits
// that gives, and what each switch does. Hold, pause, refund and revoke reach this through the row status the Play sync writes
// (PlayBillingSyncService HoldOrPauseAsync / EndedAsync: Expired, or Cancelled for revoke). WRITTEN, NOT RUN on the build machine.
public class StayLifecycleStatesTests
{
    private static readonly DateTime Now = DateTime.UtcNow;
    private static readonly DateOnly Today = Be1Kit.Today;

    private static UserPlan Row(string status, double endsInHours, bool play = false) => new()
    {
        Id = Guid.NewGuid(), UserId = Guid.NewGuid(), FeatureKey = ListingKinds.Stay, PlanId = OwnerPlanCatalog.StayPlusId,
        Source = UserPlanSources.Prepaid, Status = status, StartsAt = Now.AddDays(-30), EndsAt = Now.AddHours(endsInHours),
        PlaySubscriptionId = play ? Guid.NewGuid() : null,
    };

    private static PlanLimits LimitsOf(IEnumerable<UserPlan> rows, bool paymentOn)
    {
        var entitled = PlanRowSql.Entitled(Now).Compile();
        var row = rows.Where(entitled).OrderByDescending(r => r.EndsAt).FirstOrDefault();
        var active = row == null ? null : new PlanEntitlementService.ActiveRow(row.Id, row.UserId, row.PlanId, row.Source, row.StartsAt, row.EndsAt);
        return PlanEntitlementService.Compute(OwnerPlanCatalog.Seeded(), paymentOn ? PaymentFeatureStates.On() : PaymentFeatureStates.Off(), ListingKinds.Stay, active);
    }

    private static void AssertFreeNumbers(PlanLimits l)
    {
        Assert.Equal((2, 2, 1, 1), (l.RoomTypes, l.Photos, l.JobsPosts, l.SpecialPrices));
        Assert.Equal((4, 1, 15, 0), (l.CalendarEntries, l.BlockedRanges, l.UpcomingDays, l.MonthsAhead));
        Assert.False(l.MonthSummary);
        Assert.False(l.Reminders);
        Assert.True(l.CanUpgrade);
        Assert.False(l.IsPaid);
    }

    private static void AssertPlusNumbers(PlanLimits l)
    {
        Assert.Equal((6, 8, 4, 5), (l.RoomTypes, l.Photos, l.JobsPosts, l.SpecialPrices));
        Assert.Equal((50, 10, 183, 6), (l.CalendarEntries, l.BlockedRanges, l.UpcomingDays, l.MonthsAhead));
        Assert.True(l.MonthSummary);
        Assert.True(l.Reminders);
    }

    // ---------------------------------------------------------------- which plan row counts (L5 to L10, L13)

    [Theory]
    [InlineData(UserPlanStatuses.Active, 240.0, false, true)]      // L3 / L4 Plus running
    [InlineData(UserPlanStatuses.Active, -1.0, false, false)]      // L6 ended by the clock, first request before the sweep
    [InlineData(UserPlanStatuses.Active, -1.0, true, true)]        // L7 Play grace / late renewal: entitled until the sync ends it
    [InlineData(UserPlanStatuses.Expired, 48.0, true, false)]      // L8 hold or pause: the sync expires the row, EndsAt untouched
    [InlineData(UserPlanStatuses.Expired, -1.0, false, false)]     // L6 swept
    [InlineData(UserPlanStatuses.Cancelled, 0.0, true, false)]     // L10 refund / revoke: Cancelled, EndsAt = now
    [InlineData(UserPlanStatuses.Cancelled, 240.0, false, false)]  // admin revoke
    public void Entitled_row_per_state(string status, double endsInHours, bool play, bool entitled)
        => Assert.Equal(entitled, PlanRowSql.Entitled(Now).Compile()(Row(status, endsInHours, play)));

    [Theory]
    [InlineData(PlayStates.Active, 24.0, true)]
    [InlineData(PlayStates.InGracePeriod, -1.0, true)]
    [InlineData(PlayStates.Canceled, 24.0, true)]     // L5 cancel while Plus runs: stays until endsAt
    [InlineData(PlayStates.Canceled, -1.0, false)]
    [InlineData(PlayStates.OnHold, 24.0, false)]      // L8
    [InlineData(PlayStates.Paused, 24.0, false)]      // L8
    [InlineData(PlayStates.Expired, -1.0, false)]
    [InlineData(PlayStates.Pending, 24.0, false)]
    public void Play_state_entitlement(string state, double expiryInHours, bool entitled)
        => Assert.Equal(entitled, PlayStates.IsEntitled(state, DateTime.UtcNow.AddHours(expiryInHours)));

    [Fact]
    public void Limits_walk_free_buy_cancel_expire_resubscribe_refund_hold_off()
    {
        var oldPlan = Row(UserPlanStatuses.Active, 240);

        AssertFreeNumbers(LimitsOf([], true));                                                             // L1 / L2 Free
        var plus = LimitsOf([oldPlan], true);                                                              // L3 buy
        AssertPlusNumbers(plus);
        Assert.True(plus.IsPaid);
        Assert.False(plus.CanUpgrade);                                                                     // L4 Plus full: nothing to upgrade to
        Assert.Equal((DateTime?)oldPlan.EndsAt, plus.EndsAt);

        oldPlan.PlayAutoRenewEnabled = false;                                                              // L5 cancel, still running
        AssertPlusNumbers(LimitsOf([oldPlan], true));

        AssertFreeNumbers(LimitsOf([Row(UserPlanStatuses.Active, -1)], true));                             // L6 expire
        AssertPlusNumbers(LimitsOf([Row(UserPlanStatuses.Active, -1, play: true)], true));                 // L7 grace
        AssertFreeNumbers(LimitsOf([Row(UserPlanStatuses.Expired, 48, play: true)], true));                // L8 hold
        AssertFreeNumbers(LimitsOf([Row(UserPlanStatuses.Cancelled, 0, play: true)], true));               // L10 refund

        var expired = Row(UserPlanStatuses.Expired, -100);
        var again = Row(UserPlanStatuses.Active, 700);
        AssertPlusNumbers(LimitsOf([expired, again], true));                                               // L9 re-subscribe (old row stays Expired)
        AssertFreeNumbers(LimitsOf([expired, Row(UserPlanStatuses.Cancelled, 0)], true));

        var off = LimitsOf([], false);                                                                     // L14 payment OFF
        AssertPlusNumbers(off);
        Assert.False(off.CanUpgrade);
        Assert.False(off.IsPaid);
        Assert.False(off.PaymentEnabled);
        AssertPlusNumbers(LimitsOf([Row(UserPlanStatuses.Expired, -5)], false));
    }

    [Fact]
    public void Switch_of_cycle_keeps_the_same_numbers_so_no_row_turns_inactive()
    {
        // L12: the seeded catalog has one Plus plan per kind, so a cycle switch (1, 3, 12 months) only moves EndsAt.
        var oneMonth = LimitsOf([Row(UserPlanStatuses.Active, 24 * 30)], true);
        var twelve = LimitsOf([Row(UserPlanStatuses.Active, 24 * 365)], true);
        Assert.Equal(oneMonth with { UserPlanId = null, StartsAt = null, EndsAt = null }, twelve with { UserPlanId = null, StartsAt = null, EndsAt = null });
        var rooms = StayLifecycleKit.Rooms(6);
        Assert.Empty(PlanRowChoice.Changed(rooms, PlanRowChoice.ChooseIndexed(rooms, twelve.RoomTypes, twelve.PaymentEnabled)));
    }

    // ---------------------------------------------------------------- calendar entries: active manual rows, last added visible

    private static List<CalendarEntryRow> Entries(int activeManual)
    {
        var rows = new List<CalendarEntryRow>();
        for (var i = 0; i < activeManual; i++)
        {
            var row = Be1Kit.Manual(Today.AddDays(2 + i), Today.AddDays(3 + i));
            row.CreatedAt = StayLifecycleKit.Base.AddMinutes(i);
            rows.Add(row);
        }
        for (var i = 0; i < 2; i++)
        {
            var past = Be1Kit.Manual(Today.AddDays(-20 - i), Today.AddDays(-19 - i));
            past.CreatedAt = StayLifecycleKit.Base.AddDays(-30).AddMinutes(i);
            rows.Add(past);
        }
        rows.Add(Be1Kit.Auto(Today.AddDays(5), Today.AddDays(6)));
        rows.Add(Be1Kit.Auto(Today.AddDays(9), Today.AddDays(10), cancelled: true));
        return rows;
    }

    [Fact]
    public void Calendar_entries_walk_expire_keeps_the_last_added_visible_and_plus_shows_all()
    {
        var free = StayLifecycleStatesTestsKit.Free;
        var plus = StayLifecycleStatesTestsKit.Plus;
        var rows = Entries(10);
        var manual = rows.Where(r => r.IsActiveManual(Today)).OrderBy(r => r.CreatedAt).ToList();
        var fixedRows = rows.Where(r => !r.IsActiveManual(Today)).Select(r => r.Id).ToList();

        // L3 / L4: Plus (50) shows all ten; L14: OFF the same.
        Assert.Equal(rows.Count, OwnerCalendarRules.VisibleIds(rows, Today, plus.CalendarEntries).Count);
        Assert.Equal(rows.Count, OwnerCalendarRules.VisibleIds(rows, Today, StayLifecycleStatesTestsKit.Off.CalendarEntries).Count);

        // L6: Free keeps the last four added; past rows and app bookings stay visible; nothing is removed.
        var visible = OwnerCalendarRules.VisibleIds(rows, Today, free.CalendarEntries);
        Assert.Equal(manual.Skip(6).Select(r => r.Id).Concat(fixedRows).OrderBy(i => i).ToList(), visible.OrderBy(i => i).ToList());
        Assert.Equal(6, OwnerCalendarRules.HiddenCount(OwnerCalendarRules.ActiveManualCount(rows, Today), free.CalendarEntries, 0));
        Assert.Equal(10, OwnerCalendarRules.ActiveManualCount(rows, Today));
        // Idempotent: the same call gives the same set.
        Assert.Equal(visible.OrderBy(i => i).ToList(), OwnerCalendarRules.VisibleIds(rows, Today, free.CalendarEntries).OrderBy(i => i).ToList());

        // Deleting a visible entry frees a slot for the newest hidden one.
        var afterDelete = rows.Where(r => r.Id != manual[9].Id).ToList();
        var visibleAfter = OwnerCalendarRules.VisibleIds(afterDelete, Today, free.CalendarEntries);
        Assert.Contains(manual[5].Id, visibleAfter);
        Assert.DoesNotContain(manual[4].Id, visibleAfter);

        // L9: Plus again shows everything.
        Assert.Equal(rows.Count, OwnerCalendarRules.VisibleIds(rows, Today, plus.CalendarEntries).Count);
        Assert.Equal(0, OwnerCalendarRules.HiddenCount(10, plus.CalendarEntries, 0));
    }

    [Fact]
    public void Calendar_and_blocks_meters_report_used_above_the_limit_after_expiry_and_a_new_one_is_refused()
    {
        var free = StayLifecycleStatesTestsKit.Free;
        var meters = OwnerCalendarRules.Meters(ListingKinds.Stay, free, activeManual: 10, activeBlocks: 6, activeSpecials: 3);
        Assert.Contains((PlanFeatureKeys.CalendarEntries, 10, 4), meters);
        Assert.Contains((PlanFeatureKeys.BlockedRanges, 6, 1), meters);
        Assert.Contains((PlanFeatureKeys.SpecialPrices, 3, 1), meters);
        // Create asks used >= limit (blocks: BlockedRangeService, entries: OwnerCalendarWriter) and the refusal carries canUpgrade.
        Assert.True(10 >= free.CalendarEntries && 6 >= free.BlockedRanges);
        var blockSrc = Be3.Be3Kit.Source("RentNearBy.Infrastructure", "Services", "Owner", "Be1", "BlockedRangeService.cs");
        Assert.Contains("if (check.Active >= limit)", blockSrc);
    }

    // ---------------------------------------------------------------- upcoming days and months ahead

    [Fact]
    public void Window_per_state_free_plus_off_and_after_expiry()
    {
        var free = OwnerCalendarWindow.Of(StayLifecycleStatesTestsKit.Free, Today);
        Assert.Equal(Today.AddDays(15), free.UpcomingTo);
        Assert.Equal(new DateOnly(2026, 11, 30), free.MonthTo);
        Assert.True(free.Locked);

        var plus = OwnerCalendarWindow.Of(StayLifecycleStatesTestsKit.Plus, Today);
        Assert.Equal(Today.AddDays(183), plus.UpcomingTo);
        Assert.Equal(new DateOnly(2027, 5, 31), plus.MonthTo);
        Assert.False(plus.Locked);

        var off = OwnerCalendarWindow.Of(StayLifecycleStatesTestsKit.Off, Today);
        Assert.Equal(plus.UpcomingTo, off.UpcomingTo);
        Assert.Equal(plus.MonthTo, off.MonthTo);
        Assert.False(off.Locked);

        // L6: after expiry the window is the Free one again (data stays, only the window shrinks); L9: Plus again restores it.
        Assert.Equal(free, OwnerCalendarWindow.Of(LimitsOf([Row(UserPlanStatuses.Active, -1)], true), Today));
        Assert.Equal(plus.UpcomingTo, OwnerCalendarWindow.Of(LimitsOf([Row(UserPlanStatuses.Active, 100)], true), Today).UpcomingTo);
    }

    [Fact]
    public void Months_ahead_next_month_is_locked_on_free_open_on_plus_and_a_plain_400_while_payment_is_off_never_locked()
    {
        var next = (From: new DateOnly(2026, 12, 1), To: new DateOnly(2026, 12, 31));
        var free = OwnerCalendarWindow.Of(StayLifecycleStatesTestsKit.Free, Today);
        Assert.Equal(OwnerCalendarRules.RangeVerdict.Locked, OwnerCalendarRules.MonthRange(free, next.From, next.To).Verdict);

        var plus = OwnerCalendarWindow.Of(StayLifecycleStatesTestsKit.Plus, Today);
        Assert.Equal(OwnerCalendarRules.RangeVerdict.Ok, OwnerCalendarRules.MonthRange(plus, next.From, next.To).Verdict);
        // Beyond six months ahead, Plus is not "locked" (nothing to upgrade to).
        Assert.Equal(OwnerCalendarRules.RangeVerdict.Outside, OwnerCalendarRules.MonthRange(plus, new DateOnly(2027, 8, 1), new DateOnly(2027, 8, 31)).Verdict);
        var off = OwnerCalendarWindow.Of(StayLifecycleStatesTestsKit.Off, Today);
        Assert.Equal(OwnerCalendarRules.RangeVerdict.Outside, OwnerCalendarRules.MonthRange(off, new DateOnly(2027, 8, 1), new DateOnly(2027, 8, 31)).Verdict);
        Assert.Equal(OwnerCalendarRules.RangeVerdict.Ok, OwnerCalendarRules.MonthRange(off, next.From, next.To).Verdict);
    }

    [Fact]
    public async Task Month_read_beyond_the_free_window_answers_plan_feature_locked_before_any_query()
    {
        var reader = new OwnerCalendarReader(null!, null!, new FestivalCache(new MemoryCache(new MemoryCacheOptions())));
        var ctx = Be1Kit.Context(ListingKinds.Stay, StayLifecycleStatesTestsKit.Free);
        var result = await reader.MonthAsync(ctx, new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 31), TestContext.Current.CancellationToken);
        Assert.Equal(OwnerErrorTypes.PlanFeatureLocked, result.Error!.Type);
        Assert.Equal(403, OwnerErrorTypes.StatusOf(OwnerErrorTypes.PlanFeatureLocked));
    }

    // ---------------------------------------------------------------- month money summary and morning reminder

    [Fact]
    public async Task Month_summary_is_locked_on_free_and_after_expiry_without_touching_the_database()
    {
        var reader = new OwnerCalendarReader(null!, null!, new FestivalCache(new MemoryCache(new MemoryCacheOptions())));
        var expiredFree = LimitsOf([Row(UserPlanStatuses.Active, -1)], true);
        foreach (var limits in new[] { StayLifecycleStatesTestsKit.Free, expiredFree })
        {
            var ctx = Be1Kit.Context(ListingKinds.Stay, limits);
            var summary = await reader.SummaryAsync(ctx, null, TestContext.Current.CancellationToken);
            Assert.Equal(OwnerErrorTypes.PlanFeatureLocked, summary.Error!.Type);
            var due = await reader.BalanceDueAsync(ctx, null, null, TestContext.Current.CancellationToken);
            Assert.Equal(OwnerErrorTypes.PlanFeatureLocked, due.Error!.Type);
        }
        Assert.True(StayLifecycleStatesTestsKit.Plus.MonthSummary);
        Assert.True(StayLifecycleStatesTestsKit.Off.MonthSummary);
    }

    [Fact]
    public async Task Reminder_turn_on_is_locked_on_free_and_after_expiry_but_stored_flag_is_never_cleared_by_a_plan_pass()
    {
        var writer = new OwnerCalendarWriter(null!, NullLogger<OwnerCalendarWriter>.Instance);
        var expiredFree = LimitsOf([Row(UserPlanStatuses.Cancelled, 0)], true);
        foreach (var limits in new[] { StayLifecycleStatesTestsKit.Free, expiredFree })
        {
            var result = await writer.SetReminderAsync(Be1Kit.Context(ListingKinds.Stay, limits), true, TestContext.Current.CancellationToken);
            Assert.Equal(OwnerErrorTypes.PlanFeatureLocked, result.Error!.Type);
        }
        Assert.True(StayLifecycleStatesTestsKit.Plus.Reminders);
        Assert.True(StayLifecycleStatesTestsKit.Off.Reminders);
        Assert.False(StayLifecycleStatesTestsKit.Free.Reminders);
    }
}

internal static class StayLifecycleStatesTestsKit
{
    public static readonly PlanLimits Free = Be3.Be3Kit.Free(ListingKinds.Stay);
    public static readonly PlanLimits Plus = Be3.Be3Kit.Plus(ListingKinds.Stay);
    public static readonly PlanLimits Off = Be3.Be3Kit.Off(ListingKinds.Stay);
}
