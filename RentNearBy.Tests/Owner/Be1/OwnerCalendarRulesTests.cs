using RentNearBy.Core.Models.Owner;

namespace RentNearBy.Tests.Owner.Be1;

// Contract 11.1 BE1: label, note, phone guard, nights, one-day taxi, money, visibility, active count, hidden count, month param.
public class OwnerCalendarRulesTests
{
    private static readonly DateOnly D = Be1Kit.Today;

    private static ManualEntryInput Stay(string label = "Sharma family", int nights = 2, string? note = null, MoneyNote money = default)
        => new(label, D, D.AddDays(nights), null, null, null, note, money);

    private static ManualEntryInput Taxi(string label = "Airport drop", int days = 0, string? from = null, string? to = null)
        => new(label, D, D.AddDays(days), new TimeOnly(9, 0), from, to, null, default);

    [Theory]
    [InlineData(ListingKinds.Stay, OwnerCalendarRules.StayLabelMissing)]
    [InlineData(ListingKinds.Taxi, OwnerCalendarRules.TaxiLabelMissing)]
    public void Blank_label_asks_for_a_name_in_the_words_of_the_role(string kind, string message)
    {
        var input = kind == ListingKinds.Stay ? Stay("   ") : Taxi("  ");
        var error = OwnerCalendarRules.Validate(kind, input);
        Assert.Equal(OwnerErrorTypes.BadRequest, error!.Type);
        Assert.Equal(message, error.Message);
    }

    [Fact]
    public void Label_up_to_60_note_up_to_200_place_up_to_80()
    {
        Assert.Null(OwnerCalendarRules.Validate(ListingKinds.Stay, Stay(new string('a', 60), note: new string('n', 200))));
        Assert.Equal(OwnerCalendarRules.LabelTooLong, OwnerCalendarRules.Validate(ListingKinds.Stay, Stay(new string('a', 61)))!.Message);
        Assert.Equal(OwnerCalendarRules.NoteTooLong, OwnerCalendarRules.Validate(ListingKinds.Stay, Stay(note: new string('n', 201)))!.Message);
        Assert.Null(OwnerCalendarRules.Validate(ListingKinds.Taxi, Taxi(from: new string('p', 80))));
        Assert.Equal(OwnerCalendarRules.PlaceTooLong, OwnerCalendarRules.Validate(ListingKinds.Taxi, Taxi(to: new string('p', 81)))!.Message);
    }

    [Theory]
    [InlineData("Call 98765 43210")]
    [InlineData("98765-43210")]
    [InlineData("(987) 654.3210")]
    [InlineData("9876543210")]
    public void Ten_digits_with_separators_are_a_phone_number(string text)
    {
        Assert.Equal(OwnerErrorTypes.PhoneNotAllowed, OwnerCalendarRules.Validate(ListingKinds.Stay, Stay(label: text))!.Type);
        Assert.Equal(OwnerErrorTypes.PhoneNotAllowed, OwnerCalendarRules.Validate(ListingKinds.Stay, Stay(note: text))!.Type);
        Assert.Equal(OwnerErrorTypes.PhoneNotAllowed, OwnerCalendarRules.Validate(ListingKinds.Taxi, Taxi(from: text))!.Type);
    }

    [Theory]
    [InlineData("Room 987654321")]
    [InlineData("Rs 25,000 settled")]
    [InlineData("Booking 2026-11-21")]
    public void Nine_digits_amounts_and_dates_pass(string text)
        => Assert.Null(OwnerCalendarRules.Validate(ListingKinds.Stay, Stay(note: text)));

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(60, true)]
    [InlineData(61, false)]
    public void Stay_entry_is_1_to_60_nights(int nights, bool ok)
        => Assert.Equal(ok, OwnerCalendarRules.Validate(ListingKinds.Stay, Stay(nights: nights)) == null);

    [Fact]
    public void Taxi_trip_is_one_day()
    {
        Assert.Null(OwnerCalendarRules.Validate(ListingKinds.Taxi, Taxi(days: 0)));
        Assert.Equal(OwnerCalendarRules.TaxiOneDay, OwnerCalendarRules.Validate(ListingKinds.Taxi, Taxi(days: 1))!.Message);
    }

    [Fact]
    public void Money_errors_use_the_money_note_copy()
    {
        var error = OwnerCalendarRules.Validate(ListingKinds.Stay, Stay(money: new MoneyNote(1000, 2000, false)));
        Assert.Equal(OwnerErrorTypes.MoneyNoteInvalid, error!.Type);
        Assert.Equal(MoneyNoteMath.AdvanceAboveFinal, error.Message);
        Assert.Equal(MoneyNoteMath.FinalFirst, OwnerCalendarRules.ValidateMoney(new MoneyNote(null, null, true))!.Message);
        Assert.Equal(MoneyNoteMath.AmountRange, OwnerCalendarRules.ValidateMoney(new MoneyNote(9_900_001, null, false))!.Message);
        Assert.Null(OwnerCalendarRules.ValidateMoney(new MoneyNote(25000, 5000, false)));
    }

    [Fact]
    public void Twelve_manual_rows_on_free_show_the_four_added_last_and_hide_eight()
    {
        // Added in date order; the plan rule keeps the last added, so the four latest stay active even though they are later dates.
        var rows = Enumerable.Range(0, 12).Select(i => Added(Be1Kit.Manual(D.AddDays(i), D.AddDays(i + 1)), i)).ToList();
        var visible = OwnerCalendarRules.VisibleIds(rows, D, 4);
        Assert.Equal(rows.Skip(8).Select(r => r.Id).ToHashSet(), visible);
        Assert.Equal(8, OwnerCalendarRules.HiddenCount(OwnerCalendarRules.ActiveManualCount(rows, D), 4, 0));
    }

    [Fact]
    public void Visibility_is_recomputed_after_a_date_passes()
    {
        var rows = Enumerable.Range(0, 6).Select(i => Added(Be1Kit.Manual(D.AddDays(i), D.AddDays(i)), i)).ToList();
        var tomorrow = OwnerCalendarRules.VisibleIds(rows, D.AddDays(1), 4);
        Assert.Contains(rows[0].Id, tomorrow);                       // past now, always visible
        Assert.Equal(rows.Skip(2).Select(r => r.Id), rows.Skip(1).Where(r => tomorrow.Contains(r.Id)).Select(r => r.Id));
        Assert.DoesNotContain(rows[1].Id, tomorrow);                 // the oldest active row is the inactive one
    }

    [Fact]
    public void The_row_added_last_stays_active_whatever_its_date()
    {
        var soon = Added(Be1Kit.Manual(D.AddDays(1), D.AddDays(2)), 2);
        var later = Added(Be1Kit.Manual(D.AddDays(30), D.AddDays(31)), 5);
        var visible = OwnerCalendarRules.VisibleIds([soon, later], D, 1);
        Assert.Equal([later.Id], visible);
    }

    private static CalendarEntryRow Added(CalendarEntryRow row, int minute)
    {
        row.CreatedAt = new DateTime(2026, 11, 1, 0, minute, 0, DateTimeKind.Utc);
        return row;
    }

    [Fact]
    public void Auto_rows_are_always_visible_and_never_counted()
    {
        var manual = Enumerable.Range(0, 50).Select(i => Be1Kit.Manual(D.AddDays(i), D.AddDays(i + 1))).ToList();
        var autos = Enumerable.Range(0, 5).Select(i => Be1Kit.Auto(D.AddDays(i), D.AddDays(i + 2))).ToList();
        var all = manual.Concat(autos).ToList();
        var visible = OwnerCalendarRules.VisibleIds(all, D, 4);
        Assert.All(autos, a => Assert.Contains(a.Id, visible));
        Assert.Equal(50, OwnerCalendarRules.ActiveManualCount(all, D));
    }

    [Fact]
    public void Active_count_ends_today_counts_yesterday_does_not()
    {
        var endsToday = Be1Kit.Manual(D.AddDays(-2), D);
        var endedYesterday = Be1Kit.Manual(D.AddDays(-3), D.AddDays(-1));
        var cancelledAuto = Be1Kit.Auto(D, D.AddDays(2), cancelled: true);
        Assert.Equal(1, OwnerCalendarRules.ActiveManualCount([endsToday, endedYesterday, cancelledAuto], D));
    }

    [Fact]
    public void Hidden_count_adds_visible_rows_past_the_window()
    {
        Assert.Equal(0, OwnerCalendarRules.HiddenCount(3, 4, 0));
        Assert.Equal(2, OwnerCalendarRules.HiddenCount(3, 4, 2));
        Assert.Equal(9, OwnerCalendarRules.HiddenCount(12, 4, 1));
    }

    [Fact]
    public void Month_param_accepts_the_last_90_days_to_the_window_end()
    {
        var free = OwnerCalendarWindow.Of(15, 0, locked: true, D);
        Assert.Equal((new DateOnly(2026, 11, 1), new DateOnly(2026, 12, 1)), OwnerCalendarRules.Month(null, free));
        // D-32 / FB-44: today 21 Nov keeps rows from 23 Aug, so August is partial and the first offered month is September.
        Assert.Equal((new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 1)), OwnerCalendarRules.Month("2026-09", free));
        Assert.Null(OwnerCalendarRules.Month("2026-08", free));
        Assert.Null(OwnerCalendarRules.Month("2026-07", free));
        Assert.Null(OwnerCalendarRules.Month("2026-12", free));
        Assert.Null(OwnerCalendarRules.Month("Nov 2026", free));
        var plus = OwnerCalendarWindow.Of(183, 6, locked: false, D);
        Assert.NotNull(OwnerCalendarRules.Month("2027-05", plus));
        Assert.Null(OwnerCalendarRules.Month("2027-06", plus));
    }

    [Fact]
    public void Replay_compares_every_editable_field()
    {
        var row = Be1Kit.Manual(D, D.AddDays(2));
        row.Label = "Sharma family";
        row.FinalAmount = 9000;
        var same = new ManualEntryInput(" Sharma family ", D, D.AddDays(2), null, null, null, null, new MoneyNote(9000, null, false));
        Assert.True(OwnerCalendarRules.SameManual(row, same));
        Assert.False(OwnerCalendarRules.SameManual(row, same with { Note = "late arrival" }));
        Assert.False(OwnerCalendarRules.SameManual(row, same with { Money = new MoneyNote(9000, 1000, false) }));
    }

    [Fact]
    public void Meters_name_the_keys_of_the_role()
    {
        var stay = OwnerCalendarRules.Meters(ListingKinds.Stay, Be1Kit.Free(ListingKinds.Stay), 3, 1, 0).Select(m => m.Key).ToList();
        var taxi = OwnerCalendarRules.Meters(ListingKinds.Taxi, Be1Kit.Free(ListingKinds.Taxi), 3, 0, 0).Select(m => m.Key).ToList();
        Assert.Equal([PlanFeatureKeys.CalendarEntries, PlanFeatureKeys.BlockedRanges, PlanFeatureKeys.SpecialPrices], stay);
        Assert.Equal([PlanFeatureKeys.CalendarEntries], taxi);
    }
}
