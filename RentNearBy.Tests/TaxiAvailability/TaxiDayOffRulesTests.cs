using RentNearBy.Core.DTOs.Owner;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Services.Owner.Be1;
using RentNearBy.Tests.Owner.Be1;

namespace RentNearBy.Tests.TaxiAvailability;

// Contract 1.3 A2 checks before the lock, R9 (code points), R24 (reason update beyond the window), R25 (control characters).
public class TaxiDayOffRulesTests
{
    private static TaxiDaysOffSaveRequest Req(IEnumerable<(string Date, string? Reason)>? set = null, IEnumerable<string>? clear = null) => new()
    {
        Set = set?.Select(s => new TaxiDayOffSetItem { Date = s.Date, Reason = s.Reason }).ToList(),
        Clear = clear?.Select(c => (string?)c).ToList(),
    };

    [Fact]
    public void Empty_or_missing_lists_are_bad_request()
    {
        Assert.Equal(OwnerErrorTypes.BadRequest, TaxiDayOffRules.Parse(null).ErrorType);
        Assert.Equal(OwnerErrorTypes.BadRequest, TaxiDayOffRules.Parse(new TaxiDaysOffSaveRequest()).ErrorType);
        Assert.Equal(OwnerErrorTypes.BadRequest, TaxiDayOffRules.Parse(Req([], [])).ErrorType);
    }

    [Fact]
    public void More_than_250_items_is_bad_request_and_250_is_fine()
    {
        var start = new DateOnly(2026, 12, 1);
        var ok = Enumerable.Range(0, 250).Select(i => (TaxiSearchDate.Text(start.AddDays(i)), (string?)null));
        Assert.True(TaxiDayOffRules.Parse(Req(ok)).Ok);
        var tooMany = Enumerable.Range(0, 200).Select(i => (TaxiSearchDate.Text(start.AddDays(i)), (string?)null));
        var clear = Enumerable.Range(300, 51).Select(i => TaxiSearchDate.Text(start.AddDays(i)));
        Assert.Equal(TaxiDayOffRules.TooMany, TaxiDayOffRules.Parse(Req(tooMany, clear)).Message);
    }

    [Fact]
    public void A_date_twice_or_in_both_lists_is_bad_request()
    {
        Assert.Equal(TaxiDayOffRules.DateTwice, TaxiDayOffRules.Parse(Req([("2026-11-27", null), ("2026-11-27", "x")])).Message);
        Assert.Equal(TaxiDayOffRules.DateTwice, TaxiDayOffRules.Parse(Req([("2026-11-27", null)], ["2026-11-27"])).Message);
        Assert.Equal(TaxiDayOffRules.DateTwice, TaxiDayOffRules.Parse(Req(clear: ["2026-11-27", "2026-11-27"])).Message);
    }

    [Theory]
    [InlineData("2026-11-27T10:00")]
    [InlineData("2026/11/27")]
    [InlineData("")]
    public void A_date_that_is_not_exactly_yyyy_mm_dd_is_bad_request(string date)
    {
        Assert.Equal(TaxiDayOffRules.DateInvalid, TaxiDayOffRules.Parse(Req([(date, null)])).Message);
        Assert.Equal(TaxiDayOffRules.DateInvalid, TaxiDayOffRules.Parse(Req(clear: [date])).Message);
    }

    [Fact]
    public void Reason_is_trimmed_and_empty_becomes_null()
    {
        var parsed = TaxiDayOffRules.Parse(Req([("2026-11-27", "  Family function  "), ("2026-11-28", "   "), ("2026-11-29", null)]));
        Assert.True(parsed.Ok);
        Assert.Equal(new string?[] { "Family function", null, null }, parsed.Changes!.Set.Select(s => s.Reason));
    }

    [Fact]
    public void Control_characters_except_line_feed_are_removed()
    {
        Assert.Equal("Shaadi\nin Almora", TaxiDayOffRules.CleanReason("Sha\u0000adi\u0007\n\tin Almora\r"));
        Assert.Null(TaxiDayOffRules.CleanReason("\u0001\u0002"));
    }

    [Fact]
    public void Reason_is_counted_in_code_points_200_ok_201_refused()
    {
        var hindi = new string('श', 200);
        Assert.True(TaxiDayOffRules.Parse(Req([("2026-11-27", hindi)])).Ok);
        var tooLong = TaxiDayOffRules.Parse(Req([("2026-11-27", hindi + "श")]));
        Assert.Equal(OwnerErrorTypes.DayOffReasonTooLong, tooLong.ErrorType);
        Assert.Equal(400, OwnerErrorTypes.StatusOf(OwnerErrorTypes.DayOffReasonTooLong));
        Assert.Equal("Keep the reason under 200 characters.", tooLong.Message);
    }

    [Fact]
    public void Surrogate_pairs_count_once()
    {
        var emoji = string.Concat(Enumerable.Repeat("\U0001F600", 150));
        Assert.Equal(300, emoji.Length);
        Assert.Equal(150, TaxiDayOffRules.CodePoints(emoji));
        Assert.True(TaxiDayOffRules.Parse(Req([("2026-11-27", emoji)])).Ok);
    }

    [Fact]
    public void Past_set_dates_are_reported_in_order_and_clear_accepts_any_date()
    {
        var today = new DateOnly(2026, 11, 26);
        var parsed = TaxiDayOffRules.Parse(Req([("2026-11-25", null), ("2026-11-27", null), ("2026-11-20", null)], ["2020-01-01"]));
        Assert.True(parsed.Ok);
        Assert.Equal(new[] { new DateOnly(2026, 11, 20), new DateOnly(2026, 11, 25) }, TaxiDayOffRules.PastDates(parsed.Changes!, today));
    }

    [Fact]
    public async Task Past_day_is_refused_409_before_any_query()
    {
        var ctx = Be1Kit.Context(ListingKinds.Taxi, Be1Kit.Free(ListingKinds.Taxi));
        var changes = TaxiDayOffRules.Parse(Req([(TaxiSearchDate.Text(ctx.Today.AddDays(-1)), null)])).Changes!;
        var result = await new TaxiDayOffService(null!, Microsoft.Extensions.Logging.Abstractions.NullLogger<TaxiDayOffService>.Instance)
            .SaveAsync(ctx, changes, TestContext.Current.CancellationToken);
        Assert.Equal(OwnerErrorTypes.DayOffPast, result.Error!.Type);
        Assert.Equal(409, OwnerErrorTypes.StatusOf(OwnerErrorTypes.DayOffPast));
    }

    [Fact]
    public void Window_free_is_this_month_and_plus_is_six_months_ahead()
    {
        var free = Be1Kit.Context(ListingKinds.Taxi, Be1Kit.Free(ListingKinds.Taxi)).Window;
        var plus = Be1Kit.Context(ListingKinds.Taxi, Be1Kit.Plus(ListingKinds.Taxi)).Window;
        Assert.Equal(new DateOnly(2026, 11, 30), free.MonthTo);
        Assert.Equal(new DateOnly(2027, 5, 31), plus.MonthTo);
    }

    // The one plan key this feature enforces is monthsAhead; its single [PlanKeyTest] lives in CalendarMonthLockTests (one per key).
    [Fact]
    public void Months_ahead_decides_the_new_days_off_beyond_the_window_and_skips_days_already_off()
    {
        var free = Be1Kit.Context(ListingKinds.Taxi, Be1Kit.Free(ListingKinds.Taxi)).Window;
        var dec2 = new DateOnly(2026, 12, 2);
        var nov28 = new DateOnly(2026, 11, 28);
        Assert.Equal(new[] { dec2 }, TaxiDayOffRules.BeyondWindow([nov28, dec2], free));
        // R24: an already-off day is never passed in, so its reason update beyond the window is accepted.
        Assert.Empty(TaxiDayOffRules.BeyondWindow([nov28], free));
        var plus = Be1Kit.Context(ListingKinds.Taxi, Be1Kit.Plus(ListingKinds.Taxi)).Window;
        Assert.Empty(TaxiDayOffRules.BeyondWindow([dec2], plus));
    }

    [Fact]
    public void Free_can_upgrade_so_beyond_window_is_plan_feature_locked_and_off_is_outside_window()
    {
        Assert.True(Be1Kit.Free(ListingKinds.Taxi).CanUpgrade);
        Assert.False(Be1Kit.Off(ListingKinds.Taxi).CanUpgrade);
        Assert.Equal(403, OwnerErrorTypes.StatusOf(OwnerErrorTypes.PlanFeatureLocked));
        Assert.Equal(409, OwnerErrorTypes.StatusOf(OwnerErrorTypes.CalendarOutsideWindow));
    }

    [Fact]
    public void Month_helpers_follow_the_ist_month()
    {
        var today = new DateOnly(2026, 11, 26);
        Assert.Equal("2026-11", TaxiDayOffRules.MonthText(today));
        Assert.Equal((new DateOnly(2026, 11, 1), new DateOnly(2026, 12, 1)), TaxiDayOffRules.Month(today));
    }

    [Fact]
    public void Op_id_is_accepted_and_ignored()
    {
        var r = Req([("2026-11-27", null)]);
        r.OpId = "abc";
        Assert.True(TaxiDayOffRules.Parse(r).Ok);
    }
}
