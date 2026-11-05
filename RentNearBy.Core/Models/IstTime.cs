namespace RentNearBy.Core.Models;

// India Standard Time (UTC+05:30, no DST). Every window is half-open: [start, end).
public static class IstTime
{
    public static readonly TimeSpan Offset = TimeSpan.FromMinutes(330);
    public const int EveHour = 18;

    public static DateOnly TodayIst(DateTime nowUtc) => DateOnly.FromDateTime(nowUtc + Offset);

    public static DateTime IstMidnightUtc(DateOnly date)
        => DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue) - Offset, DateTimeKind.Utc);

    public static DateTime AtUtc(DateOnly date, int hour, int minute = 0) => IstMidnightUtc(date).AddHours(hour).AddMinutes(minute);

    public static TimeSpan TimeOfDayIst(DateTime utc) => (utc + Offset).TimeOfDay;

    public static bool InWindow(DateTime startUtc, DateTime endUtc, DateTime nowUtc) => nowUtc >= startUtc && nowUtc < endUtc;

    // An "is tomorrow" push: from hour:00 IST the day before until the date itself starts.
    public static DateTime EveDueUtc(DateOnly date, int hour = EveHour) => AtUtc(date.AddDays(-1), hour);

    public static bool InEveWindow(DateOnly date, DateTime nowUtc, int hour = EveHour)
        => InWindow(EveDueUtc(date, hour), IstMidnightUtc(date), nowUtc);

    // [fromHour, toHour) IST; wraps midnight when fromHour > toHour.
    public static bool IsQuiet(DateTime utc, int fromHour, int toHour)
    {
        if (fromHour == toHour) return false;
        var h = TimeOfDayIst(utc).TotalHours;
        return fromHour > toHour ? h >= fromHour || h < toHour : h >= fromHour && h < toHour;
    }

    // utc when outside quiet hours, else toHour:00 IST that ends the quiet period.
    public static DateTime WakeUtc(DateTime utc, int fromHour, int toHour)
    {
        if (!IsQuiet(utc, fromHour, toHour)) return utc;
        var day = TodayIst(utc);
        var wake = AtUtc(day, toHour);
        return wake > utc ? wake : AtUtc(day.AddDays(1), toHour);
    }

    // The IST dates d in [today - back, today + ahead] for which open(d, now) holds, ascending.
    public static DateOnly[] DatesWhere(DateTime nowUtc, int back, int ahead, Func<DateOnly, DateTime, bool> open)
    {
        var today = TodayIst(nowUtc);
        var list = new List<DateOnly>();
        for (var d = today.AddDays(-back); d <= today.AddDays(ahead); d = d.AddDays(1))
            if (open(d, nowUtc)) list.Add(d);
        return list.ToArray();
    }
}
