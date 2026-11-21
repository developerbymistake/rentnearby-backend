using RentNearBy.Core.Interfaces;

namespace RentNearBy.Core.Models.Owner;

public sealed record OwnerCalendarWindowValue(
    DateOnly Today,
    DateOnly UpcomingTo,
    DateOnly MonthFrom,
    DateOnly MonthTo,
    DateOnly ManualFrom,
    DateOnly ManualTo,
    DateOnly PastFrom,
    bool Locked)
{
    public bool InManualRange(DateOnly from, DateOnly to) => from >= ManualFrom && to <= ManualTo && to >= from;

    public bool InMonthRange(DateOnly day) => day >= MonthFrom && day <= MonthTo;
}

// Contract 2.0 "Window" (doc 6.1.4 and 6.1.9). Plus and payment OFF read UpcomingDays >= 183, so their manual window is today + 183.
public static class OwnerCalendarWindow
{
    public static OwnerCalendarWindowValue Of(PlanLimits limits, DateOnly today)
        => Of(limits.UpcomingDays, limits.MonthsAhead, limits.CanUpgrade, today);

    public static OwnerCalendarWindowValue Of(int upcomingDays, int monthsAhead, bool locked, DateOnly today)
    {
        var upcomingTo = today.AddDays(upcomingDays);
        var monthFrom = new DateOnly(today.Year, today.Month, 1);
        var monthTo = monthFrom.AddMonths(monthsAhead + 1).AddDays(-1);
        var manualTo = upcomingDays >= OwnerLimits.PlusWindowDays
            ? today.AddDays(OwnerLimits.PlusWindowDays)
            : upcomingTo > monthTo ? upcomingTo : monthTo;
        return new OwnerCalendarWindowValue(
            today,
            upcomingTo,
            monthFrom,
            monthTo,
            today.AddDays(-OwnerLimits.ManualBackDays),
            manualTo,
            today.AddDays(-OwnerLimits.PastDays),
            locked);
    }
}
