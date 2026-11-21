using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Services.Owner.Be1;

namespace RentNearBy.Tests.Owner.Be1;

// Contract 3.2: the enforcement tests of the calendar keys (CalendarEntries, UpcomingDays, MonthsAhead, MonthSummary). The pure
// parts run here; the limit under the listing lock is proven by the integration and race tests (written, not run on this machine).
public class CalendarLimitTests
{
    [Fact]
    [PlanKeyTest(PlanFeatureKeys.CalendarEntries)]
    public void Fifth_manual_entry_on_free_is_refused_with_the_free_line_plus_and_off_get_fifty()
    {
        var free = Be1Kit.Free(ListingKinds.Stay);
        Assert.Equal(4, free.CalendarEntries);
        Assert.True(free.CanUpgrade);
        Assert.Equal("You have 4 active entries.", OwnerErrorTypes.LimitMessage(OwnerErrorTypes.CalendarEntryLimit, free.CalendarEntries, free.CanUpgrade));

        var plus = Be1Kit.Plus(ListingKinds.Taxi);
        Assert.Equal(50, plus.CalendarEntries);
        Assert.False(plus.CanUpgrade);
        Assert.Equal("You can add up to 50 entries.", OwnerErrorTypes.LimitMessage(OwnerErrorTypes.CalendarEntryLimit, plus.CalendarEntries, plus.CanUpgrade));

        var off = Be1Kit.Off(ListingKinds.Stay);
        Assert.Equal(50, off.CalendarEntries);
        Assert.False(off.CanUpgrade);
    }

    [Fact]
    public void Limit_error_carries_key_limit_used_and_can_upgrade()
    {
        var refusal = new RentNearBy.Core.DTOs.Owner.PlanLimitRefusalDto(PlanFeatureKeys.CalendarEntries, 4, 4, true);
        Assert.Equal(("CalendarEntries", 4, 4, true), (refusal.Key, refusal.Limit, refusal.Used, refusal.CanUpgrade));
        Assert.Equal(409, OwnerErrorTypes.StatusOf(OwnerErrorTypes.CalendarEntryLimit));
    }
}

public class CalendarWindowTests
{
    [Fact]
    [PlanKeyTest(PlanFeatureKeys.UpcomingDays)]
    public void Manual_dates_must_sit_inside_the_window_free_on_the_2nd_and_28th_plus_and_off()
    {
        var free2 = Be1Kit.Context(ListingKinds.Stay, Be1Kit.Free(ListingKinds.Stay), new DateOnly(2026, 11, 2)).Window;
        Assert.True(OwnerCalendarRules.InsideWindow(free2, new DateOnly(2026, 11, 29), new DateOnly(2026, 11, 30)));
        Assert.False(OwnerCalendarRules.InsideWindow(free2, new DateOnly(2026, 11, 30), new DateOnly(2026, 12, 1)));
        Assert.True(OwnerCalendarRules.InsideWindow(free2, new DateOnly(2026, 10, 26), new DateOnly(2026, 10, 27)));
        Assert.False(OwnerCalendarRules.InsideWindow(free2, new DateOnly(2026, 10, 25), new DateOnly(2026, 10, 26)));

        var free28 = Be1Kit.Context(ListingKinds.Taxi, Be1Kit.Free(ListingKinds.Taxi), new DateOnly(2026, 11, 28)).Window;
        Assert.True(OwnerCalendarRules.InsideWindow(free28, new DateOnly(2026, 12, 13), new DateOnly(2026, 12, 13)));
        Assert.False(OwnerCalendarRules.InsideWindow(free28, new DateOnly(2026, 12, 14), new DateOnly(2026, 12, 14)));
        Assert.Equal(new DateOnly(2026, 12, 13), free28.UpcomingTo);

        var plus = Be1Kit.Context(ListingKinds.Stay, Be1Kit.Plus(ListingKinds.Stay)).Window;
        Assert.True(OwnerCalendarRules.InsideWindow(plus, Be1Kit.Today.AddDays(182), Be1Kit.Today.AddDays(183)));
        Assert.False(OwnerCalendarRules.InsideWindow(plus, Be1Kit.Today.AddDays(183), Be1Kit.Today.AddDays(184)));

        var off = Be1Kit.Context(ListingKinds.Stay, Be1Kit.Off(ListingKinds.Stay)).Window;
        Assert.Equal(plus.ManualTo, off.ManualTo);
        Assert.False(off.Locked);
    }

    [Fact]
    public async Task Writes_outside_the_window_are_refused_before_any_query()
    {
        var ctx = Be1Kit.Context(ListingKinds.Stay, Be1Kit.Free(ListingKinds.Stay));
        var writer = new OwnerCalendarWriter(null!, Microsoft.Extensions.Logging.Abstractions.NullLogger<OwnerCalendarWriter>.Instance);
        var input = new ManualEntryInput("Sharma family", Be1Kit.Today.AddDays(40), Be1Kit.Today.AddDays(42), null, null, null, null, default);
        var result = (await writer.CreateAsync(ctx, Guid.NewGuid(), input, TestContext.Current.CancellationToken));
        Assert.Equal(OwnerErrorTypes.CalendarOutsideWindow, result.Error!.Type);
        Assert.Equal(409, OwnerErrorTypes.StatusOf(result.Error.Type));
        var edit = (await writer.UpdateAsync(ctx, Guid.NewGuid(), 1, input, TestContext.Current.CancellationToken));
        Assert.Equal(OwnerErrorTypes.CalendarOutsideWindow, edit.Error!.Type);
    }
}

public class CalendarMonthLockTests
{
    [Fact]
    [PlanKeyTest(PlanFeatureKeys.MonthsAhead)]
    public void Next_month_on_free_is_locked_partly_outside_is_clamped_off_answers_400()
    {
        var free = Be1Kit.Context(ListingKinds.Stay, Be1Kit.Free(ListingKinds.Stay)).Window;
        var (verdict, _, _) = OwnerCalendarRules.MonthRange(free, new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 31));
        Assert.Equal(OwnerCalendarRules.RangeVerdict.Locked, verdict);

        var clamped = OwnerCalendarRules.MonthRange(free, new DateOnly(2026, 10, 26), new DateOnly(2026, 12, 6));
        Assert.Equal((OwnerCalendarRules.RangeVerdict.Ok, new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30)), clamped);

        var plus = Be1Kit.Context(ListingKinds.Stay, Be1Kit.Plus(ListingKinds.Stay)).Window;
        Assert.Equal(OwnerCalendarRules.RangeVerdict.Ok, OwnerCalendarRules.MonthRange(plus, new DateOnly(2027, 5, 1), new DateOnly(2027, 5, 31)).Verdict);
        Assert.Equal(OwnerCalendarRules.RangeVerdict.Outside, OwnerCalendarRules.MonthRange(plus, new DateOnly(2027, 6, 1), new DateOnly(2027, 6, 30)).Verdict);

        Assert.Equal(OwnerCalendarRules.RangeVerdict.Outside, OwnerCalendarRules.MonthRange(free, new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30)).Verdict);
        Assert.Equal(OwnerCalendarRules.RangeVerdict.TooLong, OwnerCalendarRules.MonthRange(plus, new DateOnly(2026, 11, 1), new DateOnly(2026, 12, 17)).Verdict);
        Assert.Equal(OwnerCalendarRules.RangeVerdict.BadOrder, OwnerCalendarRules.MonthRange(plus, new DateOnly(2026, 11, 5), new DateOnly(2026, 11, 4)).Verdict);
    }

    [Fact]
    public async Task Locked_month_answers_403_plan_feature_locked_before_any_query()
    {
        var reader = new OwnerCalendarReader(null!, null!, null!);
        var ctx = Be1Kit.Context(ListingKinds.Taxi, Be1Kit.Free(ListingKinds.Taxi));
        var result = (await reader.MonthAsync(ctx, new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 31), TestContext.Current.CancellationToken));
        Assert.Equal(OwnerErrorTypes.PlanFeatureLocked, result.Error!.Type);
        Assert.Equal(403, OwnerErrorTypes.StatusOf(result.Error.Type));
    }
}

public class MonthSummaryLockTests
{
    [Fact]
    [PlanKeyTest(PlanFeatureKeys.MonthSummary)]
    public async Task Summary_and_balance_due_are_locked_on_free_and_open_on_plus_and_off()
    {
        var reader = new OwnerCalendarReader(null!, null!, null!);
        foreach (var kind in new[] { ListingKinds.Stay, ListingKinds.Taxi })
        {
            var ctx = Be1Kit.Context(kind, Be1Kit.Free(kind));
            Assert.Equal(OwnerErrorTypes.PlanFeatureLocked, (await reader.SummaryAsync(ctx, null, TestContext.Current.CancellationToken)).Error!.Type);
            Assert.Equal(OwnerErrorTypes.PlanFeatureLocked, (await reader.BalanceDueAsync(ctx, null, null, TestContext.Current.CancellationToken)).Error!.Type);
        }
        Assert.True(Be1Kit.Plus(ListingKinds.Stay).MonthSummary);
        Assert.True(Be1Kit.Off(ListingKinds.Taxi).MonthSummary);
        var plusCtx = Be1Kit.Context(ListingKinds.Stay, Be1Kit.Plus(ListingKinds.Stay));
        Assert.Equal(OwnerErrorTypes.BadRequest, (await reader.SummaryAsync(plusCtx, "2025-01", TestContext.Current.CancellationToken)).Error!.Type);
    }

    [Fact]
    public async Task Reminder_switch_needs_the_plan_to_turn_on()
    {
        var writer = new OwnerCalendarWriter(null!, Microsoft.Extensions.Logging.Abstractions.NullLogger<OwnerCalendarWriter>.Instance);
        var ctx = Be1Kit.Context(ListingKinds.Taxi, Be1Kit.Free(ListingKinds.Taxi));
        Assert.Equal(OwnerErrorTypes.PlanFeatureLocked, (await writer.SetReminderAsync(ctx, true, TestContext.Current.CancellationToken)).Error!.Type);
    }
}
