using System.Globalization;

namespace RentNearBy.Core.Models;

public sealed record TaxiSearchDay(bool Ok, DateOnly Day);

// The seeker date of the taxi lists: absent = today, before today = today, after today + 90 or malformed = InvalidDate.
public static class TaxiSearchDate
{
    public const string InvalidDateType = "InvalidDate";
    public const string InvalidDateMessage = "Pick a date up to 90 days ahead.";

    public static TaxiSearchDay Resolve(string? date, DateOnly today)
    {
        if (string.IsNullOrEmpty(date)) return new(true, today);
        if (!TryParse(date, out var day)) return new(false, today);
        if (day < today) return new(true, today);
        return day > today.AddDays(TaxiLimits.SearchDaysAhead) ? new(false, today) : new(true, day);
    }

    public static bool TryParse(string? text, out DateOnly day)
    {
        day = default;
        return text is { Length: 10 } && DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day);
    }

    public static string Text(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string KeyPart(DateOnly day) => ":d" + day.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
}
