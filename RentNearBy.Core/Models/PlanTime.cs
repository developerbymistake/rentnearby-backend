namespace RentNearBy.Core.Models;

// Plan time rules: IST calendar for display and reports, calendar months for Google Play periods, and one
// rounding rule for whole-day bookkeeping columns.
public static class PlanTime
{
    public static readonly TimeSpan IstOffset = TripExecutionRules.IstOffset;

    // Google may clamp a period end to a shorter month (31 Jan + P1M = 28 Feb), at most 3 days.
    private static readonly TimeSpan AnchorSlack = TimeSpan.FromDays(3);

    public static DateOnly IstDate(DateTime utc) => DateOnly.FromDateTime(utc + IstOffset);

    public static DateTime IstMidnightUtc(DateOnly date) => TripExecutionRules.IstMidnightUtc(date);

    public static DateTime IstMonthStartUtc(DateTime nowUtc)
    {
        var today = IstDate(nowUtc);
        return IstMidnightUtc(new DateOnly(today.Year, today.Month, 1));
    }

    // Calendar days between the IST dates of now and EndsAt; 0 = ends today (or already ended).
    public static int DaysLeft(DateTime endsAtUtc, DateTime nowUtc)
        => endsAtUtc <= nowUtc ? 0 : Math.Max(0, IstDate(endsAtUtc).DayNumber - IstDate(nowUtc).DayNumber);

    // Nearest whole day, half a day rounds up: the error is at most 12 h per event and has no bias.
    public static int WholeDays(TimeSpan span)
        => span <= TimeSpan.Zero ? 0 : (int)Math.Min(int.MaxValue, (span.Ticks + TimeSpan.TicksPerDay / 2) / TimeSpan.TicksPerDay);

    // Days of the calendar cycle that starts at startUtc (31 Jan + 1 month = 28 days, 1 Feb 2028 + 1 month = 29 days).
    public static int CalendarCycleDays(DateTime startUtc, int cycleMonths)
        => cycleMonths <= 0 ? 0 : WholeDays(startUtc.AddMonths(cycleMonths) - startUtc);

    // Days of one Google Play billing period that ends at endUtc. A known start inside one calendar cycle is
    // exact; otherwise the period is the calendar cycle that ends at endUtc.
    public static int PlayPeriodDays(DateTime? startUtc, DateTime endUtc, int cycleMonths)
    {
        var earliest = endUtc.AddMonths(-Math.Max(1, cycleMonths)) - AnchorSlack;
        if (startUtc is { } start && start < endUtc && start >= earliest)
            return Math.Max(1, WholeDays(endUtc - start));
        return Math.Max(1, CalendarCycleDays(endUtc.AddMonths(-Math.Max(1, cycleMonths)), cycleMonths));
    }
}
