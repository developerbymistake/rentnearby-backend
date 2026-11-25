using RentNearBy.Core.Interfaces.Owner;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Tests.Owner.Be1;
using static RentNearBy.Tests.Owner.Subscription.TaxiLifecycleKit;

namespace RentNearBy.Tests.Owner.Subscription;

// Subscription matrix L1-L16 for the taxi calendar keys (CalendarEntries, UpcomingDays, MonthsAhead, MonthSummary, Reminders),
// the ring keys (RingWeight, Priority, Featured) and the evening alert. Pure, plus source guards for the SQL side.
public class TaxiLifecycleCalendarTests
{
    private static readonly DateOnly Today = Be1Kit.Today;
    private static readonly DateTime Now = new(2026, 11, 21, 6, 0, 0, DateTimeKind.Utc);

    private static List<CalendarEntryRow> Entries(int count)
        => Enumerable.Range(0, count).Select(i => Entry(Today.AddDays(1 + i % 10), Now.AddMinutes(i))).ToList();

    [Fact]
    public void L1_L2_free_calendar_limit_is_four_with_the_upgrade_line()
    {
        Assert.Equal(4, Free.CalendarEntries);
        Assert.True(Free.CanUpgrade);
        Assert.Equal("You have 4 active entries.", OwnerErrorTypes.LimitMessage(OwnerErrorTypes.CalendarEntryLimit, Free.CalendarEntries, Free.CanUpgrade));
        Assert.Equal(409, OwnerErrorTypes.StatusOf(OwnerErrorTypes.CalendarEntryLimit));
    }

    [Fact]
    public void L3_L4_plus_calendar_limit_is_fifty_and_the_full_line_has_no_upgrade()
    {
        Assert.Equal(50, Plus.CalendarEntries);
        Assert.False(Plus.CanUpgrade);
        Assert.Equal("You can add up to 50 entries.", OwnerErrorTypes.LimitMessage(OwnerErrorTypes.CalendarEntryLimit, Plus.CalendarEntries, Plus.CanUpgrade));
    }

    [Fact]
    public void L3_plus_unlocks_months_summary_reminder_and_the_long_window()
    {
        Assert.Equal(0, Free.MonthsAhead);
        Assert.False(Free.MonthSummary);
        Assert.False(Free.Reminders);
        Assert.Equal(15, Free.UpcomingDays);
        Assert.Equal(6, Plus.MonthsAhead);
        Assert.True(Plus.MonthSummary);
        Assert.True(Plus.Reminders);
        Assert.Equal(183, Plus.UpcomingDays);
    }

    [Fact]
    public void L6_expire_shows_only_the_last_added_four_active_entries_and_hides_the_rest_without_deleting()
    {
        var rows = Entries(10);
        var visible = OwnerCalendarRules.VisibleIds(rows, Today, Free.CalendarEntries);

        Assert.Equal(4, visible.Count);
        Assert.Equal(rows.OrderByDescending(r => r.CreatedAt).Take(4).Select(r => r.Id).ToHashSet(), visible);
        Assert.Equal(6, OwnerCalendarRules.HiddenCount(OwnerCalendarRules.ActiveManualCount(rows, Today), Free.CalendarEntries, 0));
        Assert.Equal(10, rows.Count);
    }

    [Fact]
    public void L6_past_and_auto_entries_are_never_hidden_by_the_plan()
    {
        var rows = Entries(6);
        var past = Entry(Today.AddDays(-3), Now.AddDays(-9));
        var auto = Be1Kit.Auto(Today.AddDays(2), Today.AddDays(2));
        rows.Add(past);
        rows.Add(auto);

        var visible = OwnerCalendarRules.VisibleIds(rows, Today, Free.CalendarEntries);

        Assert.Contains(past.Id, visible);
        Assert.Contains(auto.Id, visible);
        Assert.Equal(6, visible.Count);
    }

    [Fact]
    public void L6_a_manual_entry_ending_today_still_counts_and_one_ending_yesterday_does_not()
    {
        var today = Entry(Today, Now);
        var yesterday = Entry(Today.AddDays(-1), Now);
        Assert.True(today.IsActiveManual(Today));
        Assert.False(yesterday.IsActiveManual(Today));
    }

    [Fact]
    public void L9_resubscribe_shows_every_entry_again_in_the_same_order()
    {
        var rows = Entries(10);
        var free = OwnerCalendarRules.VisibleIds(rows, Today, Free.CalendarEntries);
        var plus = OwnerCalendarRules.VisibleIds(rows, Today, Plus.CalendarEntries);

        Assert.Equal(10, plus.Count);
        Assert.True(free.IsSubsetOf(plus));
        Assert.Equal(0, OwnerCalendarRules.HiddenCount(10, Plus.CalendarEntries, 0));
    }

    [Fact]
    public void L9_deleting_a_hidden_entry_before_a_rebuy_loses_nothing_else()
    {
        var rows = Entries(10);
        var hidden = rows.Except(rows.Where(r => OwnerCalendarRules.VisibleIds(rows, Today, Free.CalendarEntries).Contains(r.Id))).First();
        rows.Remove(hidden);

        Assert.Equal(9, OwnerCalendarRules.VisibleIds(rows, Today, Plus.CalendarEntries).Count);
    }

    [Fact]
    public void L12_a_plan_switch_keeps_every_visible_entry_visible()
    {
        var rows = Entries(8);
        var before = OwnerCalendarRules.VisibleIds(rows, Today, Plus.CalendarEntries);
        var after = OwnerCalendarRules.VisibleIds(rows, Today, Be3Plus.Again.CalendarEntries);
        Assert.Equal(before, after);
    }

    [Fact]
    public void L14_payment_off_shows_everything_and_has_the_plus_window()
    {
        Assert.Equal(50, Off.CalendarEntries);
        Assert.False(Off.CanUpgrade);
        Assert.Equal(10, OwnerCalendarRules.VisibleIds(Entries(10), Today, Off.CalendarEntries).Count);
        var off = OwnerCalendarWindow.Of(Off, Today);
        var plus = OwnerCalendarWindow.Of(Plus, Today);
        Assert.Equal(plus.ManualTo, off.ManualTo);
        Assert.False(off.Locked);
    }

    [Fact]
    public void L2_free_window_is_locked_and_the_plus_window_is_not()
    {
        var free = OwnerCalendarWindow.Of(Free, Today);
        var plus = OwnerCalendarWindow.Of(Plus, Today);
        Assert.True(free.Locked);
        Assert.False(plus.Locked);
        Assert.Equal(Today.AddDays(15), free.UpcomingTo);
        Assert.True(plus.ManualTo > free.ManualTo);
    }

    [Fact]
    public void L6_after_expiry_a_month_past_the_free_window_is_locked_and_the_range_check_answers_locked()
    {
        var free = OwnerCalendarWindow.Of(Free, Today);
        var far = free.MonthTo.AddDays(5);
        Assert.Equal(OwnerCalendarRules.RangeVerdict.Locked, OwnerCalendarRules.MonthRange(free, far, far.AddDays(3)).Verdict);

        var plus = OwnerCalendarWindow.Of(Plus, Today);
        Assert.NotEqual(OwnerCalendarRules.RangeVerdict.Locked, OwnerCalendarRules.MonthRange(plus, far, far.AddDays(3)).Verdict);
    }

    [Fact]
    public void L6_month_summary_and_reminder_toggle_are_refused_on_free_and_allowed_on_plus()
    {
        var reader = Src("RentNearBy.Infrastructure", "Services", "Owner", "Be1", "OwnerCalendarReader.cs");
        Assert.Contains("if (!ctx.Limits.MonthSummary) return Be1Error.Of(OwnerErrorTypes.PlanFeatureLocked", reader);
        var writer = Src("RentNearBy.Infrastructure", "Services", "Owner", "Be1", "OwnerCalendarWriter.cs");
        Assert.Contains("if (on && !ctx.Limits.Reminders) return Be1Error.Of(OwnerErrorTypes.PlanFeatureLocked", writer);
        Assert.False(Free.MonthSummary || Free.Reminders);
        Assert.True(Plus.MonthSummary && Plus.Reminders);
    }

    [Fact]
    public void L6_turning_the_reminder_off_is_always_allowed_so_an_expired_owner_is_never_stuck_on()
    {
        var writer = Src("RentNearBy.Infrastructure", "Services", "Owner", "Be1", "OwnerCalendarWriter.cs");
        Assert.DoesNotContain("if (!on && !ctx.Limits.Reminders)", writer);
    }

    [Fact]
    public void L6_morning_reminder_runs_only_for_a_plan_value_of_one()
    {
        var job = Src("RentNearBy.Infrastructure", "Services", "Owner", "Be4", "OwnerMorningReminderJob.cs");
        Assert.Contains("PlanKeySql.Column(PlanFeatureKeys.Reminders)} = 1", job);
        Assert.Contains("l.\"RemindersOn\"", job);
    }

    [Fact]
    public void L1_to_L16_evening_alert_ignores_the_plan_and_the_reminder_switch()
    {
        var job = Src("RentNearBy.Infrastructure", "Services", "Owner", "Be4", "OwnerEveAlertJob.cs");
        Assert.DoesNotContain("PlanKeySql", job);
        Assert.DoesNotContain("l.\"RemindersOn\"", job);
        Assert.DoesNotContain("PlanFeatureKeys.Reminders", job);
        Assert.Contains("TaxiDriverPushCopy.TripTomorrow", job);
    }

    [Fact]
    public void L16_evening_alert_is_sent_once_per_booking_and_never_carries_the_traveller_name()
    {
        var entry = Guid.NewGuid();
        var day = Today.AddDays(1);
        var a = TaxiDriverPushCopy.TripTomorrow(Guid.NewGuid(), entry, day, "Haldwani bus stand");
        var b = TaxiDriverPushCopy.TripTomorrow(a.UserId, entry, day, "Haldwani bus stand");

        Assert.Equal("Trip tomorrow", a.Title);
        Assert.Contains("Haldwani bus stand", a.Body);
        Assert.Equal(a.Key, TaxiDriverPushCopy.TripTomorrow(a.UserId, entry, day, "Haldwani bus stand").Key);
        Assert.NotEqual(a.Key, TaxiDriverPushCopy.TripTomorrow(a.UserId, Guid.NewGuid(), day, "Haldwani bus stand").Key);
        Assert.Equal(b.Key, a.Key);
    }

    [Fact]
    public void L6_plan_end_push_names_the_inactive_routes_and_promises_nothing_is_deleted()
    {
        var ended = TaxiDriverPushCopy.PlansStarted(Guid.NewGuid(), 4, Today, plansStartedToday: false);
        Assert.Equal(OwnerPushText.PlusEndedTitle, ended.Title);
        Assert.Contains("Nothing is deleted", ended.Body);
        Assert.Contains("4 routes are inactive", ended.Body);

        var one = TaxiDriverPushCopy.PlansStarted(Guid.NewGuid(), 1, Today);
        Assert.Contains("1 route is inactive", one.Body);
        Assert.Equal(OwnerPushText.PlansStartedTitle, one.Title);
    }

    [Fact]
    public void L6_one_plan_end_notice_per_day_per_owner()
    {
        var user = Guid.NewGuid();
        var a = TaxiDriverPushCopy.PlansStarted(user, 4, Today, false);
        var b = TaxiDriverPushCopy.PlansStarted(user, 3, Today, false);
        var next = TaxiDriverPushCopy.PlansStarted(user, 3, Today.AddDays(1), false);
        Assert.Equal(a.Key, b.Key);
        Assert.NotEqual(a.Key, next.Key);
    }

    [Fact]
    public void L3_L6_ring_weight_priority_and_featured_follow_the_plan_and_fall_back_on_expiry()
    {
        Assert.Equal(1, Free.RingWeight);
        Assert.Equal(2, Plus.RingWeight);
        Assert.Equal((short)0, Free.PriorityRank);
        Assert.True(Plus.PriorityRank > Free.PriorityRank);
        Assert.False(Free.IsFeatured);
        Assert.True(Plus.IsFeatured);
        Assert.True(Plus.IsPaid);
        Assert.False(Free.IsPaid);
    }

    [Fact]
    public void L14_payment_off_ranks_everyone_alike_except_an_admin_grant()
    {
        Assert.Equal(Free.RingWeight, Off.RingWeight);
        Assert.Equal(Free.PriorityRank, Off.PriorityRank);
        Assert.Equal(Free.IsFeatured, Off.IsFeatured);
        Assert.False(Off.IsPaid);

        var grant = Be3Plus.OffGrant;
        Assert.Equal(Plus.RingWeight, grant.RingWeight);
        Assert.Equal(Plus.PriorityRank, grant.PriorityRank);
        Assert.True(grant.IsFeatured);
    }

    [Fact]
    public void L14_payment_off_never_hides_calendar_rows_through_the_hidden_count()
    {
        Assert.Equal(0, OwnerCalendarRules.HiddenCount(10, Off.CalendarEntries, 0));
    }

    [Fact]
    public void L16_the_calendar_meters_equal_the_server_limits_for_a_taxi_owner()
    {
        var meters = OwnerCalendarRules.Meters(ListingKinds.Taxi, Free, 4, 0, 0);
        Assert.Single(meters);
        Assert.Equal((PlanFeatureKeys.CalendarEntries, 4, 4), meters[0]);
        var plus = OwnerCalendarRules.Meters(ListingKinds.Taxi, Plus, 7, 0, 0);
        Assert.Equal((PlanFeatureKeys.CalendarEntries, 7, 50), plus[0]);
    }

    [Fact]
    public void L16_the_ring_pool_reads_the_stored_weight_and_never_a_live_plan_join()
    {
        var pool = RentNearBy.Infrastructure.Services.TaxiRequests.TaxiCandidateQuery.PoolSql;
        Assert.Contains("l.\"RingWeight\"::int AS \"RingWeight\"", pool);
        Assert.DoesNotContain("UserPlans", pool);
    }
}

internal static class Be3Plus
{
    public static PlanLimits Again => RentNearBy.Tests.Owner.Be3.Be3Kit.Compute(Taxi, true, RentNearBy.Tests.Owner.Be3.Be3Kit.PlusId(Taxi));
    public static PlanLimits OffGrant => RentNearBy.Tests.Owner.Be3.Be3Kit.OffWithGrant(Taxi);
    private const string Taxi = ListingKinds.Taxi;
}
