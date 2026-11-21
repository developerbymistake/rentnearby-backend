using RentNearBy.Core.Models.Owner;

namespace RentNearBy.Tests.Owner.Foundation;

// Pure shared helpers of the foundation: calendar window, cursors, phone guard, error types and limit messages.
public class OwnerHelpersTests
{
    [Fact]
    public void Free_window_on_the_2nd_takes_the_month_end()
    {
        var w = OwnerCalendarWindow.Of(15, 0, locked: true, new DateOnly(2026, 11, 2));
        Assert.Equal(new DateOnly(2026, 11, 17), w.UpcomingTo);
        Assert.Equal(new DateOnly(2026, 11, 1), w.MonthFrom);
        Assert.Equal(new DateOnly(2026, 11, 30), w.MonthTo);
        Assert.Equal(new DateOnly(2026, 10, 26), w.ManualFrom);
        Assert.Equal(new DateOnly(2026, 11, 30), w.ManualTo);
        Assert.Equal(new DateOnly(2026, 8, 4), w.PastFrom);
        Assert.True(w.Locked);
    }

    [Fact]
    public void Free_window_on_the_28th_takes_the_upcoming_days()
    {
        var w = OwnerCalendarWindow.Of(15, 0, locked: true, new DateOnly(2026, 11, 28));
        Assert.Equal(new DateOnly(2026, 12, 13), w.UpcomingTo);
        Assert.Equal(new DateOnly(2026, 11, 30), w.MonthTo);
        Assert.Equal(new DateOnly(2026, 12, 13), w.ManualTo);
    }

    [Fact]
    public void Plus_and_payment_off_window_is_183_days_and_six_months()
    {
        var w = OwnerCalendarWindow.Of(183, 6, locked: false, new DateOnly(2026, 11, 21));
        Assert.Equal(new DateOnly(2027, 5, 23), w.UpcomingTo);
        Assert.Equal(new DateOnly(2027, 5, 31), w.MonthTo);
        Assert.Equal(new DateOnly(2027, 5, 23), w.ManualTo);
        Assert.False(w.Locked);
        Assert.True(w.InManualRange(new DateOnly(2026, 11, 14), new DateOnly(2026, 11, 15)));
        Assert.False(w.InManualRange(new DateOnly(2026, 11, 13), new DateOnly(2026, 11, 15)));
        Assert.True(w.InMonthRange(new DateOnly(2027, 5, 31)));
        Assert.False(w.InMonthRange(new DateOnly(2026, 10, 31)));
    }

    [Fact]
    public void Window_reads_plan_limits()
    {
        var limits = RentNearBy.Infrastructure.Services.PlanEntitlementService.Compute(
            Support.Owner.OwnerPlanCatalog.Seeded(), PaymentFeatureStates.Off(), ListingKinds.Taxi, null);
        var w = OwnerCalendarWindow.Of(limits, new DateOnly(2026, 11, 21));
        Assert.Equal(new DateOnly(2027, 5, 23), w.ManualTo);
        Assert.False(w.Locked);
    }

    [Fact]
    public void Date_and_number_cursors_round_trip_and_reject_garbage()
    {
        var id = Guid.NewGuid();
        Assert.True(OwnerCursors.TryDecodeDate(OwnerCursors.EncodeDate(new DateOnly(2026, 12, 31), id), out var d, out var i1));
        Assert.Equal((new DateOnly(2026, 12, 31), id), (d, i1));
        Assert.True(OwnerCursors.TryDecodeNumber(OwnerCursors.EncodeNumber(4500, id), out var n, out var i2));
        Assert.Equal((4500L, id), (n, i2));
        Assert.False(OwnerCursors.TryDecodeDate(OwnerCursors.EncodeNumber(4500, id), out _, out _));
        Assert.False(OwnerCursors.TryDecodeNumber(OwnerCursors.EncodeDate(new DateOnly(2026, 1, 1), id), out _, out _));
        Assert.False(OwnerCursors.TryDecodeDate("not-a-cursor!", out _, out _));
        Assert.False(OwnerCursors.TryDecodeDate(null, out _, out _));
        Assert.False(OwnerCursors.TryDecodeDate(new string('a', 300), out _, out _));
        Assert.DoesNotContain('=', OwnerCursors.EncodeDate(new DateOnly(2026, 1, 1), id));
    }

    [Theory]
    [InlineData("Call 98765 43210", true)]
    [InlineData("98765-43210", true)]
    [InlineData("(987) 654.3210", true)]
    [InlineData("+91 98765 43210", true)]
    [InlineData("9876543210", true)]
    [InlineData("987654321", false)]
    [InlineData("Rs 25,000", false)]
    [InlineData("Room 12, 2 guests", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Phone_guard_refuses_ten_digits_in_a_row(string? text, bool expected)
        => Assert.Equal(expected, PhoneGuard.HasPhone(text));

    [Fact]
    public void Any_has_phone_checks_every_field()
    {
        Assert.True(PhoneGuard.AnyHasPhone("Neha", null, "98765 43210"));
        Assert.False(PhoneGuard.AnyHasPhone("Neha", null, "Rs 25,000"));
    }

    [Theory]
    [InlineData(OwnerErrorTypes.PlanPhotoLimit, 2, true, "Free plan allows 2 photos.")]
    [InlineData(OwnerErrorTypes.PlanPhotoLimit, 8, false, "You can add up to 8 photos.")]
    [InlineData(OwnerErrorTypes.PlanRoomTypeLimit, 2, true, "2 room types on Free.")]
    [InlineData(OwnerErrorTypes.PlanRoomTypeLimit, 6, false, "You can add up to 6 room types.")]
    [InlineData(OwnerErrorTypes.TaxiRouteLimit, 1, true, "1 route on Free.")]
    [InlineData(OwnerErrorTypes.TaxiRouteLimit, 5, false, "You can add up to 5 routes.")]
    [InlineData(OwnerErrorTypes.StayJobLimit, 1, true, "Your plan allows 1 active job. Close one or upgrade.")]
    [InlineData(OwnerErrorTypes.StayJobLimit, 4, false, "You can have up to 4 active jobs. Close one to post another.")]
    [InlineData(OwnerErrorTypes.CalendarEntryLimit, 4, true, "You have 4 active entries.")]
    [InlineData(OwnerErrorTypes.CalendarEntryLimit, 50, false, "You can add up to 50 entries.")]
    [InlineData(OwnerErrorTypes.BlockedRangeLimit, 1, true, "Free allows 1 blocked range.")]
    [InlineData(OwnerErrorTypes.BlockedRangeLimit, 10, false, "You can add up to 10 blocked ranges.")]
    [InlineData(OwnerErrorTypes.SpecialPriceLimit, 1, true, "1 special price on Free.")]
    [InlineData(OwnerErrorTypes.SpecialPriceLimit, 5, false, "You can add up to 5 special prices.")]
    public void Limit_messages_follow_appendix_a_and_r6(string type, int limit, bool canUpgrade, string expected)
        => Assert.Equal(expected, OwnerErrorTypes.LimitMessage(type, limit, canUpgrade));

    [Theory]
    [InlineData(OwnerErrorTypes.ListingNotFound, 404, "Set up your listing first.")]
    [InlineData(OwnerErrorTypes.PlanFeatureLocked, 403, "This is part of Plus.")]
    [InlineData(OwnerErrorTypes.CalendarOutsideWindow, 409, "Pick a date inside your calendar.")]
    [InlineData(OwnerErrorTypes.BlockedRangeInvalid, 400, "Block 1 to 90 nights, starting within 180 days.")]
    [InlineData(OwnerErrorTypes.SpecialPriceSavesLimit, 409, "You can change special prices 3 times a day. Try again tomorrow.")]
    [InlineData(OwnerErrorTypes.PlanVisibleLimit, 409, "You can change this 3 times a day.")]
    [InlineData(OwnerErrorTypes.PhoneNotAllowed, 400, "Do not add phone numbers.")]
    [InlineData(OwnerErrorTypes.StaleEdit, 409, "This changed on another phone. Pull to refresh.")]
    [InlineData(OwnerErrorTypes.StayFullOnDates, 409, "This stay is full on your dates.")]
    [InlineData(OwnerErrorTypes.ChangeTouchesBlock, 409, "These days are blocked by you. Accept anyway?")]
    [InlineData(OwnerErrorTypes.StayNeedsPhoto, 409, "Keep at least one photo.")]
    [InlineData(OwnerErrorTypes.PlanValueBelowFree, 400, "Plus must be the same or higher than Free.")]
    [InlineData(OwnerErrorTypes.AnnounceTooSoon, 400, "Pick a date at least 7 days ahead.")]
    public void Error_types_have_the_appendix_status_and_message(string type, int status, string message)
    {
        Assert.Equal(status, OwnerErrorTypes.StatusOf(type));
        Assert.Equal(message, OwnerErrorTypes.MessageOf(type));
    }

    [Fact]
    public void Room_type_duplicate_names_the_room_and_special_price_lines_are_exact()
    {
        Assert.Equal("You already have a Deluxe room. Pick another type.", OwnerErrorTypes.RoomTypeDuplicateMessage("Deluxe"));
        Assert.Equal("A special price can cover up to 60 nights.", OwnerErrorTypes.SpecialPriceTooLong);
        Assert.Equal("Start date must be within 180 days.", OwnerErrorTypes.SpecialPriceStartTooLate);
        Assert.Equal("Pick a percent between 5 and 50.", OwnerErrorTypes.SpecialPricePercentLower);
        Assert.Equal("Pick a percent between 5 and 100.", OwnerErrorTypes.SpecialPricePercentHigher);
    }

    [Fact]
    public void Rate_limits_are_60_reads_and_30_writes_a_minute()
    {
        var id = Guid.NewGuid();
        Assert.Equal((60, TimeSpan.FromMinutes(1)), (OwnerRateLimitKeys.Read(id).Max, OwnerRateLimitKeys.Read(id).Window));
        Assert.Equal(30, OwnerRateLimitKeys.Write(id).Max);
        Assert.NotEqual(OwnerRateLimitKeys.Read(id).Key, OwnerRateLimitKeys.Write(id).Key);
    }

    [Fact]
    public void Constants_equal_contract_1_5()
    {
        Assert.Equal((60, 200, 80, 60), (OwnerLimits.LabelMax, OwnerLimits.NoteMax, OwnerLimits.PlaceMax, OwnerLimits.ReasonMax));
        Assert.Equal((1, 9_900_000), (OwnerLimits.MoneyMin, OwnerLimits.MoneyMax));
        Assert.Equal((7, 183, 60, 90, 180), (OwnerLimits.ManualBackDays, OwnerLimits.PlusWindowDays, OwnerLimits.StayEntryMaxNights, OwnerLimits.BlockMaxNights, OwnerLimits.BlockStartWithinDays));
        Assert.Equal((60, 180, 3, 3), (OwnerLimits.SpecialMaxNights, OwnerLimits.SpecialStartWithinDays, OwnerLimits.SpecialSavesPerDay, OwnerLimits.PlanVisibleSavesPerDay));
        Assert.Equal((90, 20, 30, 21, 7), (OwnerLimits.PastDays, OwnerLimits.PageSize, OwnerLimits.StaysChipWithinDays, OwnerLimits.FestivalStayLeadDays, OwnerLimits.FestivalTaxiLeadDays));
        Assert.Equal((10, 56, 5), (OwnerLimits.WeeklyMinShown, OwnerLimits.StatsKeepDays, OwnerLimits.RankBandKm));
        Assert.Equal((90, 90, 30, 30, 30), (OwnerLimits.CalendarDays, OwnerLimits.BlockedDays, OwnerLimits.SpecialDays, OwnerLimits.FestivalMarkDays, OwnerLimits.ReminderRunDays));
        Assert.Equal((8, 12, 10), (OwnerLimits.RoomsCeiling, OwnerLimits.PhotosCeiling, OwnerLimits.RoutesCeiling));
        Assert.Equal(100, PlanRowBands.HiddenBase);
        Assert.Equal((OwnerLimits.RoomsCeiling, OwnerLimits.PhotosCeiling, OwnerLimits.RoutesCeiling),
            (PlanKeyCatalog.Of(PlanFeatureKeys.RoomTypes).Max, PlanKeyCatalog.Of(PlanFeatureKeys.Photos).Max, PlanKeyCatalog.Of(PlanFeatureKeys.Routes).Max));
    }

    [Fact]
    public void Daily_counter_reads_zero_on_another_ist_day()
    {
        var today = new DateOnly(2026, 11, 21);
        Assert.Equal(2, RentNearBy.Infrastructure.Services.Owner.OwnerContext.CountToday(today, 2, today));
        Assert.Equal(0, RentNearBy.Infrastructure.Services.Owner.OwnerContext.CountToday(today.AddDays(-1), 3, today));
        Assert.Equal(0, RentNearBy.Infrastructure.Services.Owner.OwnerContext.CountToday(null, 0, today));
    }
}
