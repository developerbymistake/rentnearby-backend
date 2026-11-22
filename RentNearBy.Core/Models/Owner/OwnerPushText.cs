using System.Globalization;
using System.Text.Json;

namespace RentNearBy.Core.Models.Owner;

// Kind-agnostic text helpers of the owner push copy (contract 5.3): bodies cut at 120 characters with "...", dates "6 Nov", times "9:00 am".
public static class OwnerPushText
{
    public const int BodyMax = 120;

    // FB-39 (D-12): the PlanLimitsChanged title.
    public const string PlansStartedTitle = "Plans have started";
    public const string PlusEndedTitle = "Plus has ended";

    public static string LimitsTitle(bool plansStartedToday) => plansStartedToday ? PlansStartedTitle : PlusEndedTitle;
    private static readonly CultureInfo En = CultureInfo.InvariantCulture;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Cut(string text)
    {
        text = text.Trim();
        return text.Length <= BodyMax ? text : text[..(BodyMax - 3)].TrimEnd() + "...";
    }

    public static string Day(DateOnly d) => d.ToString("d MMM", En);

    public static string Range(DateOnly from, DateOnly to)
    {
        if (from == to) return Day(from);
        return from.Month == to.Month && from.Year == to.Year
            ? $"{from.Day.ToString(En)} to {Day(to)}"
            : $"{Day(from)} to {Day(to)}";
    }

    public static string Time(TimeOnly t)
    {
        var hour = t.Hour % 12 == 0 ? 12 : t.Hour % 12;
        return $"{hour.ToString(En)}:{t.Minute.ToString("00", En)} {(t.Hour < 12 ? "am" : "pm")}";
    }

    public static string Count(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n.ToString(En)} {many}";

    public static string Weeks(int days)
    {
        var weeks = Math.Max(1, (int)Math.Round(days / 7.0, MidpointRounding.AwayFromZero));
        return weeks == 1 ? "1 week" : $"{weeks.ToString(En)} weeks";
    }

    public static string Args(params (string Key, object? Value)[] args)
        => JsonSerializer.Serialize(args.ToDictionary(a => a.Key, a => a.Value), Json);

    // 12:00 IST of the given IST day, as UTC (the morning reminders expire then).
    public static DateTime NoonIstUtc(DateOnly day) => day.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Unspecified).AddHours(-5.5);

    public static string Ymd(DateOnly d) => d.ToString("yyyyMMdd", En);

    // Joins the parts with ", " while the result still fits the body; the rest are dropped (the count is in the title).
    public static string JoinFitting(IEnumerable<string> parts)
    {
        var result = string.Empty;
        foreach (var part in parts.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            var next = result.Length == 0 ? part.Trim() : result + ", " + part.Trim();
            if (next.Length > BodyMax) return result.Length == 0 ? Cut(next) : result;
            result = next;
        }
        return result;
    }
}

// One line of a morning reminder: a calendar row of today.
public sealed record OwnerReminderLine(string Kind, string Label, string? Detail, TimeOnly? TimeOfDay);
