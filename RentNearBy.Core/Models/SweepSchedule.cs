namespace RentNearBy.Core.Models;

// When a time-driven sweep should run next: at the earliest due instant it knows of (just after it, since the sweep's
// predicate is strict "ValidUntil < now()"), never later than the regular anchor, never sooner than MinGap.
public static class SweepSchedule
{
    public static readonly TimeSpan MinGap = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan AfterDue = TimeSpan.FromSeconds(1);

    public static DateTime NextWakeUtc(DateTime nowUtc, DateTime nextAnchorUtc, DateTime? earliestDueUtc)
    {
        var wake = nextAnchorUtc;
        if (earliestDueUtc is { } due && due + AfterDue < wake) wake = due + AfterDue;
        var floor = nowUtc + MinGap;
        return wake < floor ? floor : wake;
    }

    // The next hh:anchorMinute UTC strictly after now.
    public static DateTime NextHourlyAnchorUtc(DateTime nowUtc, int anchorMinute)
    {
        var target = new DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day, nowUtc.Hour, anchorMinute, 0, DateTimeKind.Utc);
        return nowUtc < target ? target : target.AddHours(1);
    }
}
