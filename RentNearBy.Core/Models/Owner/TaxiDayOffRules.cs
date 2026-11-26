using System.Globalization;
using System.Text;
using RentNearBy.Core.DTOs.Owner;

namespace RentNearBy.Core.Models.Owner;

public sealed record TaxiDayOffSet(DateOnly Date, string? Reason);

public sealed record TaxiDayOffChanges(IReadOnlyList<TaxiDayOffSet> Set, IReadOnlyList<DateOnly> Clear);

public sealed record TaxiDayOffParse(TaxiDayOffChanges? Changes, string? ErrorType, string? Message)
{
    public bool Ok => Changes != null;
}

// Contract 1.3 A2 checks before any lock: shape (400 BadRequest) then reason (400 DayOffReasonTooLong). The past check needs
// today and runs next (409 DayOffPast); window and booked days are decided under the taxi lock.
public static class TaxiDayOffRules
{
    public const string NothingToSave = "Nothing to save.";
    public const string TooMany = "Too many days in one save.";
    public const string DateTwice = "A day is listed twice.";
    public const string DateInvalid = "Pick a valid date.";

    public static TaxiDayOffParse Parse(TaxiDaysOffSaveRequest? request)
    {
        var setItems = request?.Set ?? [];
        var clearItems = request?.Clear ?? [];
        if (setItems.Count == 0 && clearItems.Count == 0) return Bad(NothingToSave);
        if (setItems.Count + clearItems.Count > OwnerLimits.TaxiDayOffChangesMax) return Bad(TooMany);

        var set = new List<TaxiDayOffSet>(setItems.Count);
        var seen = new HashSet<DateOnly>();
        foreach (var item in setItems)
        {
            if (item == null || !TaxiSearchDate.TryParse(item.Date, out var day)) return Bad(DateInvalid);
            if (!seen.Add(day)) return Bad(DateTwice);
            set.Add(new TaxiDayOffSet(day, item.Reason));
        }
        var clear = new List<DateOnly>(clearItems.Count);
        var cleared = new HashSet<DateOnly>();
        foreach (var text in clearItems)
        {
            if (!TaxiSearchDate.TryParse(text, out var day)) return Bad(DateInvalid);
            if (seen.Contains(day) || !cleared.Add(day)) return Bad(DateTwice);
            clear.Add(day);
        }

        for (var i = 0; i < set.Count; i++)
        {
            var reason = CleanReason(set[i].Reason);
            if (reason != null && CodePoints(reason) > OwnerLimits.TaxiDayOffReasonMax)
                return new TaxiDayOffParse(null, OwnerErrorTypes.DayOffReasonTooLong, OwnerErrorTypes.MessageOf(OwnerErrorTypes.DayOffReasonTooLong));
            set[i] = set[i] with { Reason = reason };
        }
        return new TaxiDayOffParse(new TaxiDayOffChanges(set, clear), null, null);
    }

    // R25: control characters except line feed removed, then trimmed; empty = null.
    public static string? CleanReason(string? reason)
    {
        if (reason == null) return null;
        var sb = new StringBuilder(reason.Length);
        foreach (var rune in reason.EnumerateRunes())
        {
            if (rune.Value != '\n' && Rune.GetUnicodeCategory(rune) == UnicodeCategory.Control) continue;
            sb.Append(rune.ToString());
        }
        var text = sb.ToString().Trim();
        return text.Length == 0 ? null : text;
    }

    public static int CodePoints(string text) => text.EnumerateRunes().Count();

    public static IReadOnlyList<DateOnly> PastDates(TaxiDayOffChanges changes, DateOnly today)
        => changes.Set.Where(s => s.Date < today).Select(s => s.Date).OrderBy(d => d).ToList();

    // A day not already off and after the plan window end (R24: an already-off day may take a reason update anywhere).
    public static IReadOnlyList<DateOnly> BeyondWindow(IEnumerable<DateOnly> notAlreadyOff, OwnerCalendarWindowValue window)
        => notAlreadyOff.Where(d => d > window.MonthTo).OrderBy(d => d).ToList();

    public static string MonthText(DateOnly today) => today.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    public static (DateOnly First, DateOnly Next) Month(DateOnly today)
    {
        var first = new DateOnly(today.Year, today.Month, 1);
        return (first, first.AddMonths(1));
    }

    public static IReadOnlyList<string> Texts(IEnumerable<DateOnly> days) => days.Select(TaxiSearchDate.Text).ToList();

    private static TaxiDayOffParse Bad(string message) => new(null, OwnerErrorTypes.BadRequest, message);
}
