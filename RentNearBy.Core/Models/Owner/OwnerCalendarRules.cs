using System.Globalization;
using System.Text.Json;
using RentNearBy.Core.Interfaces;

namespace RentNearBy.Core.Models.Owner;

// One CalendarEntries row as read by the owner calendar (contract 2.0). Owner-only: never mapped into a traveller or admin DTO.
public sealed class CalendarEntryRow
{
    public Guid Id { get; set; }
    public Guid OwnerUserId { get; set; }
    public string ListingKind { get; set; } = string.Empty;
    public Guid ListingId { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string? SourceType { get; set; }
    public Guid? SourceId { get; set; }
    public Guid? TravellerUserId { get; set; }
    public DateOnly DateFrom { get; set; }
    public DateOnly DateTo { get; set; }
    public TimeOnly? TimeOfDay { get; set; }
    public string? FromPlace { get; set; }
    public string? ToPlace { get; set; }
    public string Label { get; set; } = string.Empty;
    public string? Detail { get; set; }
    public string? Note { get; set; }
    public int? ListedTotal { get; set; }
    public int? AgreedTotal { get; set; }
    public short? Guests { get; set; }
    public short? Seats { get; set; }
    public string? RoomNames { get; set; }
    public string? PlacesJson { get; set; }
    public int? FinalAmount { get; set; }
    public int? AdvanceReceived { get; set; }
    public bool PaidInFull { get; set; }
    public bool Cancelled { get; set; }
    public bool KeepVisible { get; set; }
    public string? CancelledBy { get; set; }
    public DateOnly? CancelledOn { get; set; }
    public int Version { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool? SourceAvailable { get; set; }
    // A manual row above the plan limit (month read only): shown greyed, Delete only; edits answer PlanRowInactive.
    public bool InactiveByPlan { get; set; }

    public bool IsManual => Kind == CalendarEntryKinds.Manual;

    public MoneyNote Money => new(FinalAmount, AdvanceReceived, PaidInFull);

    // Active = still counts against the plan limit: a manual row whose last day is today or later (contract 3.2, doc 6.1.10).
    public bool IsActiveManual(DateOnly today) => IsManual && DateTo >= today;
}

// The owner's typed booking (C5, C6), already trimmed; stay rows carry no time or places.
public sealed record ManualEntryInput(
    string Label,
    DateOnly DateFrom,
    DateOnly DateTo,
    TimeOnly? TimeOfDay,
    string? FromPlace,
    string? ToPlace,
    string? Note,
    MoneyNote Money,
    bool KeepVisible = false);

public sealed record CalendarRuleError(string Type, string Message);

// Derived wire values of one entry (contract 2.0, AU-10, R-1).
public static class CalendarEntryView
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static MoneyNoteView Money(CalendarEntryRow row) => MoneyNoteMath.View(row.Money);

    public static bool TravellerRemoved(CalendarEntryRow row) => row.Kind == CalendarEntryKinds.Auto && row.TravellerUserId == null;

    // Wire time is "HH:mm" (contract 0.3.2).
    public static string? Time(CalendarEntryRow row) => row.TimeOfDay?.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    public static IReadOnlyList<string> Places(CalendarEntryRow row)
    {
        if (string.IsNullOrWhiteSpace(row.PlacesJson)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<string>>(row.PlacesJson, Json)?.Where(p => !string.IsNullOrWhiteSpace(p)).ToList() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static List<CalendarEntryRow> ParseRows(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        return JsonSerializer.Deserialize<List<CalendarEntryRow>>(json, Json) ?? [];
    }

    public static CalendarEntryRow? ParseRow(string? json)
        => string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<CalendarEntryRow>(json, Json);
}

// Doc 6.1 rules of the owner calendar (lane BE1). Pure.
public static class OwnerCalendarRules
{
    public const string StayLabelMissing = "Add a name for this booking.";
    public const string TaxiLabelMissing = "Add a name for this trip.";
    public const string LabelTooLong = "Keep the name under 60 characters.";
    public const string NoteTooLong = "Keep the note under 200 characters.";
    public const string PlaceTooLong = "Keep the place under 80 characters.";
    public const string StayNights = "Pick 1 to 60 nights.";
    public const string TaxiOneDay = "A trip is one day.";
    public const string RangeTooLong = "Pick up to 45 days.";
    public const string RangeOrder = "The end date must be after the start date.";
    public const string MonthOutside = "Pick a month inside your calendar.";
    public const string RangeOutside = "Pick dates inside your calendar.";

    public static string? Clean(string? text)
    {
        var t = text?.Trim();
        return string.IsNullOrEmpty(t) ? null : t;
    }

    // null = valid. Order: label, lengths, phone guard, dates, money. Window checks are separate (they answer 409).
    public static CalendarRuleError? Validate(string listingKind, ManualEntryInput input)
    {
        var label = input.Label.Trim();
        if (label.Length == 0)
            return Bad(listingKind == ListingKinds.Taxi ? TaxiLabelMissing : StayLabelMissing);
        if (label.Length > OwnerLimits.LabelMax) return Bad(LabelTooLong);
        if (input.Note is { Length: > OwnerLimits.NoteMax }) return Bad(NoteTooLong);
        if (input.FromPlace is { Length: > OwnerLimits.PlaceMax } || input.ToPlace is { Length: > OwnerLimits.PlaceMax }) return Bad(PlaceTooLong);
        if (PhoneGuard.AnyHasPhone(label, input.Note, input.FromPlace, input.ToPlace))
            return new CalendarRuleError(OwnerErrorTypes.PhoneNotAllowed, OwnerErrorTypes.MessageOf(OwnerErrorTypes.PhoneNotAllowed));
        if (listingKind == ListingKinds.Stay)
        {
            var nights = input.DateTo.DayNumber - input.DateFrom.DayNumber;
            if (nights < 1 || nights > OwnerLimits.StayEntryMaxNights) return Bad(StayNights);
        }
        else if (input.DateTo != input.DateFrom)
        {
            return Bad(TaxiOneDay);
        }
        if (MoneyNoteMath.Validate(input.Money) is { } moneyError)
            return new CalendarRuleError(OwnerErrorTypes.MoneyNoteInvalid, moneyError);
        return null;
    }

    public static CalendarRuleError? ValidateMoney(MoneyNote note)
        => MoneyNoteMath.Validate(note) is { } e ? new CalendarRuleError(OwnerErrorTypes.MoneyNoteInvalid, e) : null;

    // Manual dates must sit inside [today - 7, manualTo] (doc 6.1.4).
    public static bool InsideWindow(OwnerCalendarWindowValue window, DateOnly from, DateOnly to) => window.InManualRange(from, to);

    // D-07 / FB-04: on an edit each date either stays as stored or moves inside the manual window. The SQL form is
    // (@fromOk OR c."DateFrom" = @from) AND (@toOk OR c."DateTo" = @to) with the two flags from InWindow.
    public static bool DatesAllowed(OwnerCalendarWindowValue window, DateOnly storedFrom, DateOnly storedTo, DateOnly newFrom, DateOnly newTo)
        => (newFrom == storedFrom || InWindow(window, newFrom)) && (newTo == storedTo || InWindow(window, newTo)) && newTo >= newFrom;

    public static bool InWindow(OwnerCalendarWindowValue window, DateOnly day) => day >= window.ManualFrom && day <= window.ManualTo;

    // D-07: an entry whose stored end is before today is read-only except its money note and Delete.
    public static bool Ended(DateOnly storedTo, DateOnly today) => storedTo < today;

    // Doc 6.1.10 visibility: Auto always, past rows always, and the `limit` active manual rows added last (CreatedAt DESC, Id).
    public static HashSet<Guid> VisibleIds(IEnumerable<CalendarEntryRow> rows, DateOnly today, int limit)
    {
        var all = rows.ToList();
        var visible = all.Where(r => !r.IsActiveManual(today)).Select(r => r.Id).ToHashSet();
        foreach (var r in all.Where(r => r.IsActiveManual(today)).OrderByDescending(r => r.CreatedAt).ThenBy(r => r.Id).Take(Math.Max(0, limit)))
            visible.Add(r.Id);
        return visible;
    }

    public static int ActiveManualCount(IEnumerable<CalendarEntryRow> rows, DateOnly today) => rows.Count(r => r.IsActiveManual(today));

    // hiddenCount (2.1): active manual rows above the limit, plus visible active manual rows past the window end.
    public static int HiddenCount(long activeManual, int limit, long visibleBeyondWindow)
        => (int)Math.Max(0, activeManual - Math.Max(0, limit)) + (int)Math.Max(0, visibleBeyondWindow);

    public static IReadOnlyList<(string Key, int Used, int Limit)> Meters(string listingKind, PlanLimits limits, long activeManual, long activeBlocks, long activeSpecials)
    {
        var list = new List<(string, int, int)> { (PlanFeatureKeys.CalendarEntries, (int)activeManual, limits.CalendarEntries) };
        if (listingKind == ListingKinds.Stay)
        {
            list.Add((PlanFeatureKeys.BlockedRanges, (int)activeBlocks, limits.BlockedRanges));
            list.Add((PlanFeatureKeys.SpecialPrices, (int)activeSpecials, limits.SpecialPrices));
        }
        return list;
    }

    public enum RangeVerdict { Ok, TooLong, BadOrder, Locked, Outside }

    // C1 range (2.1): at most 45 days; wholly after the window = 403 PlanFeatureLocked when an upgrade is possible, else 400;
    // wholly before it = 400; partly outside = clamped to the window.
    public static (RangeVerdict Verdict, DateOnly From, DateOnly To) MonthRange(OwnerCalendarWindowValue window, DateOnly from, DateOnly to)
    {
        if (to < from) return (RangeVerdict.BadOrder, from, to);
        if (to.DayNumber - from.DayNumber > OwnerLimits.CalendarRangeMaxDays) return (RangeVerdict.TooLong, from, to);
        if (from > window.MonthTo) return (window.Locked ? RangeVerdict.Locked : RangeVerdict.Outside, from, to);
        if (to < window.MonthFrom) return (RangeVerdict.Outside, from, to);
        var f = from < window.MonthFrom ? window.MonthFrom : from;
        var t = to > window.MonthTo ? window.MonthTo : to;
        return (RangeVerdict.Ok, f, t);
    }

    // C9 / C10 month: "YYYY-MM", from the month of today - 90 days to the month of monthTo. null = invalid.
    public static (DateOnly Start, DateOnly End)? Month(string? month, OwnerCalendarWindowValue window)
    {
        DateOnly start;
        if (string.IsNullOrWhiteSpace(month))
        {
            start = new DateOnly(window.Today.Year, window.Today.Month, 1);
        }
        else if (DateOnly.TryParseExact(month.Trim() + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            start = parsed;
        }
        else
        {
            return null;
        }
        // D-32 / FB-44: months are offered from the first FULL month the 90-day clean-up still keeps, so no month shows partial sums.
        var earliest = FirstFullKeptMonth(window.Today);
        var latest = new DateOnly(window.MonthTo.Year, window.MonthTo.Month, 1);
        if (start < earliest || start > latest) return null;
        return (start, start.AddMonths(1));
    }

    public static DateOnly FirstFullKeptMonth(DateOnly today)
    {
        var past = today.AddDays(-OwnerLimits.PastDays);
        var start = new DateOnly(past.Year, past.Month, 1);
        return past.Day == 1 ? start : start.AddMonths(1);
    }

    // Replay test of C6: the stored row already holds what the request asks for.
    public static bool SameManual(CalendarEntryRow row, ManualEntryInput input)
        => row.Label == input.Label.Trim() && row.DateFrom == input.DateFrom && row.DateTo == input.DateTo && row.TimeOfDay == input.TimeOfDay
           && row.FromPlace == input.FromPlace && row.ToPlace == input.ToPlace && row.Note == input.Note && SameMoney(row, input.Money)
           && row.KeepVisible == input.KeepVisible;

    public static bool SameMoney(CalendarEntryRow row, MoneyNote note)
        => row.FinalAmount == note.FinalAmount && row.AdvanceReceived == note.AdvanceReceived && row.PaidInFull == note.PaidInFull;

    private static CalendarRuleError Bad(string message) => new(OwnerErrorTypes.BadRequest, message);
}
