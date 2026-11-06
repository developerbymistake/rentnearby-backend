using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Models;

// Per-city grouping and travel counters for the Summary record, My Trips and the d-3 push (CONTRACT_MCD D1, D2, D4).
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

    // My Trips "Travel {settled} of {segments}" (mock Q15): every car of a planned journey, 1 per own travel (settled),
    // 1 per segment with no travel yet (open). Settled cars are confirmed by both on the travel date, or his own.
    public static (int Segments, int Settled) Counters(TripPlan plan, IReadOnlyList<TripEnquiry> enquiries)
    {
        var legs = plan.Legs ?? new List<TripCityLeg>();
        if (legs.Count <= 1) return (0, 0);
        var map = TripBookingMap.Build(plan, enquiries);
        int total = 0, settled = 0;
        for (var k = 2; k <= legs.Count; k++)
        {
            var cars = map.SegmentAny.Count(s => IsLeg(s, k) && s.Car > 0);
            if (cars > 0)
            {
                total += cars;
                settled += map.SegmentSettled.Count(s => IsLeg(s, k) && s.Car > 0);
                continue;
            }
            total++;
            if (map.SegmentOwn.Contains((TripStepRoles.Segment, k, 0))) settled++;
        }
        return (total, settled);
    }

    // "Travel booked." | "1 travel not booked." | null (one city, or no travel chosen yet). Own travel counts as booked.
    public static string? TravelSentence(TripPlan plan, IReadOnlyList<TripEnquiry> enquiries)
    {
        var legs = plan.Legs ?? new List<TripCityLeg>();
        if (legs.Count <= 1) return null;
        var settled = SettledSegments(plan, TripBookingMap.Build(plan, enquiries));
        var planned = settled.Keys.Where(k => legs[k - 1]?.Arrival != null).ToList();
        if (planned.Count == 0) return null;
        var open = planned.Count(k => !settled[k]);
        return open == 0 ? "Travel booked." : $"{open} travel not booked.";
    }

    public static string StateOf(TripPlan plan, IReadOnlyList<TripEnquiry> enquiries, int legIndex)
    {
        var legs = plan.Legs ?? new List<TripCityLeg>();
        var days = LegDays(plan, legIndex);
        var arrival = legIndex >= 2 && legIndex <= legs.Count ? legs[legIndex - 1]?.Arrival : null;
        var start = plan.Settings?.StartDate;
        var count = plan.Days?.Count ?? 0;
        var legRows = enquiries
            .Where(e => e.Status != TripEnquiryStatuses.Closed && !TripSegmentRows.IsSegment(e))
            .Where(e => TripBookingMap.DaysOf(e, start, count).Any(days.Contains))
            .ToList();
        var segmentRows = arrival is { Mode: TripTravelModes.Journey, JourneyId: { } journeyId }
            ? TripSegmentRows.OfJourney(enquiries, journeyId).Where(e => e.Status != TripEnquiryStatuses.Closed).ToList()
            : new List<TripEnquiry>();

        var planDays = plan.DaysOf(legIndex).ToList();
        var spots = planDays.Sum(d => d.Spots?.Count ?? 0);
        var chosen = planDays.Any(d => d.Stay != null || d.Taxi != null) || arrival != null
            || legRows.Count > 0 || segmentRows.Count > 0;
        if (spots == 0 && !chosen) return TripBookingStates.Draft;

        var map = TripBookingMap.Build(plan, enquiries);
        var stayAny = map.StayAny.Where(days.Contains).ToHashSet();
        var staySettled = map.StaySettled.Where(days.Contains).ToHashSet();
        var taxiAny = map.TaxiAny.Where(days.Contains).ToHashSet();
        var taxiSettled = map.TaxiSettled.Where(days.Contains).ToHashSet();
        var segAny = map.SegmentAny.Where(s => legIndex >= 2 && IsLeg(s, legIndex)).ToHashSet();
        var segSettled = map.SegmentSettled.Where(s => legIndex >= 2 && IsLeg(s, legIndex)).ToHashSet();

        var open = legRows.Any(TripBookingStates.IsOpenForCompletion) || segmentRows.Any(TripBookingStates.IsOpenForCompletion);
        if (open || !stayAny.IsSubsetOf(staySettled) || !taxiAny.IsSubsetOf(taxiSettled) || !segAny.IsSubsetOf(segSettled))
            return TripBookingStates.Confirming;
        return staySettled.Count + taxiSettled.Count + segSettled.Count(s => s.Car > 0) > 0
            ? TripBookingStates.Ready
            : TripBookingStates.PlanOnly;
    }

    // Leg index (>= 2) -> booked: own travel, or every car of the current journey own, booked by him or confirmed on the travel date.
    private static Dictionary<int, bool> SettledSegments(TripPlan plan, TripBookingMap map)
    {
        var legs = plan.Legs ?? new List<TripCityLeg>();
        var result = new Dictionary<int, bool>();
        for (var k = 2; k <= legs.Count; k++)
        {
            var arrival = legs[k - 1]?.Arrival;
            var travelDay = plan.LegStart(k);
            if (arrival == null || travelDay < 1)
            {
                result[k] = false;
                continue;
            }
            if (arrival.Mode != TripTravelModes.Journey)
            {
                result[k] = true;
                continue;
            }
            var cars = Math.Clamp(arrival.Cars ?? 1, 1, JourneyRules.MaxLegs);
            result[k] = arrival.JourneyId != null
                && Enumerable.Range(1, cars).All(car => map.SegmentSettled.Contains((TripStepRoles.Segment, k, car))
                    || map.SegmentBookedByYou.Contains((TripStepRoles.Segment, k, car)));
        }
        return result;
    }

    private static bool IsLeg((string Role, int Leg, int Car) key, int legIndex)
        => key.Role == TripStepRoles.Segment && key.Leg == legIndex;
}
