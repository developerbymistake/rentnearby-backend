using RentNearBy.Core.Models;

namespace RentNearBy.Api.Handlers;

public static class JourneyGuardErrors
{
    public const string TravelDateNotReached = "TravelDateNotReached";
    public const string JourneyNotAsked = "JourneyNotAsked";
    public const string JourneyNotBooked = "JourneyNotBooked";
    public const string JourneyInTrip = JourneyErrorTypes.JourneyInTrip;
    public const string SameDriverConsecutive = "SameDriverConsecutive";
    public const string LegsDoNotChain = "LegsDoNotChain";
}

// Pure decisions of the journey handlers (testable without a database). Each returns null when the action is allowed.
public static class JourneyGuards
{
    // B5: Mark done only on or after the travel day, with every car answered by its driver with a fare or booked. A journey that
    // belongs to a trip completes only through the trip.
    public static string? CompleteBlock(DateOnly travelDate, DateOnly todayIst, bool inTrip, IEnumerable<(bool Asked, string LegStatus)> legs)
    {
        if (inTrip) return JourneyGuardErrors.JourneyInTrip;
        if (travelDate > todayIst) return JourneyGuardErrors.TravelDateNotReached;
        var list = legs.ToList();
        if (list.Count == 0 || list.All(l => !l.Asked)) return JourneyGuardErrors.JourneyNotAsked;
        return list.All(l => l.Asked && l.LegStatus == JourneyLegStatuses.Free) ? null : JourneyGuardErrors.JourneyNotBooked;
    }

    // M19: one driver is one car; the same driver on neighbouring legs is refused (legs ordered by index).
    public static bool SameDriverOnNeighbours(IReadOnlyList<(int LegIndex, Guid DriverId)> legs)
    {
        var ordered = legs.OrderBy(l => l.LegIndex).ToList();
        for (var i = 1; i < ordered.Count; i++)
            if (ordered[i].DriverId == ordered[i - 1].DriverId) return true;
        return false;
    }

    // M19 for ReplaceDriver: the new driver must differ from both neighbours of the leg.
    public static bool ClashesWithNeighbour(int legIndex, Guid driverId, IEnumerable<(int LegIndex, Guid DriverId)> legs)
        => legs.Any(l => (l.LegIndex == legIndex - 1 || l.LegIndex == legIndex + 1) && l.DriverId == driverId);

    // m15: leg keys must chain (the end of one is the start of the next) and run from the plan's first to its last place.
    public static bool LegKeysChain(IReadOnlyList<(int LegIndex, string LegKey)> legs, IReadOnlyList<string> planKeys)
    {
        var ordered = legs.OrderBy(l => l.LegIndex).ToList();
        var parsed = new List<(string From, string To)>();
        foreach (var l in ordered)
        {
            if (!TaxiWaysKeys.TryParseLegKey(l.LegKey, out var from, out var to)) return false;
            parsed.Add((from, to));
        }
        for (var i = 1; i < parsed.Count; i++)
            if (parsed[i].From != parsed[i - 1].To) return false;
        if (planKeys.Count >= 2 && (parsed[0].From != planKeys[0] || parsed[^1].To != planKeys[^1])) return false;
        return true;
    }

    // m2: three open leads per leg on every ask path.
    public static bool LegHasRoom(int openOnLeg, int pendingOnLeg) => openOnLeg + pendingOnLeg < JourneyRules.MaxAskPerLeg;

    // m2: a trip-less ask keeps the journey's own date.
    public static bool AskDateMatches(DateOnly journeyDate, DateOnly requestDate) => journeyDate == requestDate;

    // m17: a lead past its expiry takes no fare, new or edited.
    public static bool FareAllowed(string status, DateTime expiresAt, DateTime nowUtc)
        => (status == TripEnquiryStatuses.Asked || status == TripEnquiryStatuses.FareSent) && expiresAt > nowUtc;
}
