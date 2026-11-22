namespace RentNearBy.Core.Models.Owner;

// FB-27 (SEC1-02): a public stay read takes checkIn only inside [today - 1, today + 365] (IST); anything else would reach
// the date math (DateOnly overflow) and come back as a framework exception.
public static class StayDateRules
{
    public const int MaxDaysAhead = 365;
    public const string CheckInOutOfRange = "Pick a check-in date within the next 365 days.";

    public static string? CheckInError(DateOnly? checkIn, DateOnly todayIst)
        => checkIn is { } d && (d < todayIst.AddDays(-1) || d > todayIst.AddDays(MaxDaysAhead)) ? CheckInOutOfRange : null;
}
