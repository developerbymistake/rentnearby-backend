using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Models;

// Per-city grouping of nights and taxi days for the Summary record.
public static class TripSummaryLegs
{
    public static HashSet<int> LegDays(TripPlan plan, int legIndex)
        => plan.DaysOf(legIndex).Select(d => d.Day).ToHashSet();

    public static int LegOfNight(TripPlan plan, int night) => plan.LegIndexOf(night);

    // Taxi days are dates: a dated row covers CheckIn + (d - first day), as in TripBookingMap.DaysOf.
    public static List<int> LegOfTaxi(TripPlan plan, DateOnly? start, IReadOnlyList<int> days, DateOnly? checkIn)
    {
        var planDays = new List<int>();
        if (days.Count > 0 && start is { } from && checkIn is { } a)
        {
            var first = days.Min();
            planDays.AddRange(days.Select(d => a.AddDays(d - first).DayNumber - from.DayNumber + 1));
        }
        else if (days.Count > 0) planDays.AddRange(days);
        else if (start is { } s && checkIn is { } c) planDays.Add(c.DayNumber - s.DayNumber + 1);

        var count = plan.Days?.Count ?? 0;
        var legs = planDays
            .Where(d => d >= 1 && d <= count)
            .Select(plan.LegIndexOf)
            .Distinct()
            .OrderBy(k => k)
            .ToList();
        return legs.Count > 0 ? legs : [1];
    }
}
