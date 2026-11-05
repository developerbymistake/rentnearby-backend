using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Models;

// Trip steps (Getting there, Coming back, Starting from). Stored intent lives in PlanJson; the display is derived.
public static class TripStepRules
{
    public const double SameCityMetres = 5000;
    public const int NameMax = 80;

    public static TripStep? StepOf(TripSteps? steps, string role)
        => role == TripStepRoles.ComingBack ? steps?.ComingBack : steps?.GettingThere;

    public static string StatusOf(TripStep? step)
        => step?.Status is { } s && TripStepStatuses.All.Contains(s) ? s : TripStepStatuses.NotStarted;

    // Getting there: the step's own date, else the trip start. Coming back: its own date, else check-out (start + dayCount).
    public static DateOnly? ExpectedDate(string role, TripPlan plan)
    {
        if (StepOf(plan.Steps, role)?.Date is { } own) return own;
        return role == TripStepRoles.ComingBack ? plan.CheckOutDate : plan.Settings?.StartDate;
    }

    public static bool DateChanged(DateOnly? expected, DateOnly travel) => expected is { } e && e != travel;

    public static bool StartChanged(TripStartFrom? start, double lat, double lon)
        => start != null && GeoMath.HaversineKm(start.Lat, start.Lon, lat, lon) * 1000 > SameCityMetres;

    // Getting there leaves from Starting from; Coming back ends there.
    public static bool StartChanged(string role, TripStartFrom? start, SavedJourney j)
        => role == TripStepRoles.ComingBack
            ? StartChanged(start, (double)j.ToLatitude, (double)j.ToLongitude)
            : StartChanged(start, (double)j.FromLatitude, (double)j.FromLongitude);

    public static string Display(TripStep? step, SavedJourney? journey)
    {
        var status = StatusOf(step);
        switch (status)
        {
            case TripStepStatuses.Chosen:
            case TripStepStatuses.Booked:
                if (journey == null || journey.IsDeleted || step?.JourneyId != journey.Id) return TripStepDisplays.NotStarted;
                if (status == TripStepStatuses.Booked) return TripStepDisplays.Booked;
                return journey.Legs.Any(l => l.LastEnquiryId != null) ? TripStepDisplays.Asked : TripStepDisplays.Chosen;
            case TripStepStatuses.Own:
            case TripStepStatuses.Skipped:
            case TripStepStatuses.NotNeeded:
                return status;
            default:
                return TripStepDisplays.NotStarted;
        }
    }

    public static bool IsSorted(string display) => display is TripStepDisplays.Chosen or TripStepDisplays.Asked
        or TripStepDisplays.Booked or TripStepDisplays.Own or TripStepDisplays.Skipped or TripStepDisplays.NotNeeded;

    // Only when we know he starts elsewhere and nothing was done; chosen, own, skipped, notNeeded and booked silence it.
    public static bool NeedsNudge(TripSteps? steps)
        => steps?.StartFrom != null && StatusOf(steps.GettingThere) == TripStepStatuses.NotStarted;

    public static bool NeedsLink(TripStep? step) => StatusOf(step) is TripStepStatuses.Chosen or TripStepStatuses.Booked;

    // Ids the steps may still reference (before the ownership check).
    public static List<Guid> ReferencedIds(TripSteps? steps)
    {
        var ids = new List<Guid>();
        if (steps?.GettingThere?.JourneyId is { } a) ids.Add(a);
        if (steps?.ComingBack?.JourneyId is { } b && !ids.Contains(b)) ids.Add(b);
        return ids;
    }

    // Drops a journey that is not the caller's live one, a journey on a step that is not chosen/booked, and a journey
    // already used by Getting there; a chosen/booked step left without a journey goes back to notStarted.
    // Mutates steps and returns the journey ids to link to the trip.
    public static List<Guid> Sanitise(TripSteps? steps, ISet<Guid> owned)
    {
        var keep = new List<Guid>();
        if (steps == null) return keep;
        SanitiseStep(steps.GettingThere, owned, keep);
        SanitiseStep(steps.ComingBack, owned, keep);
        return keep;
    }

    private static void SanitiseStep(TripStep? step, ISet<Guid> owned, List<Guid> keep)
    {
        if (step == null) return;
        if (step.JourneyId is { } id && (!NeedsLink(step) || !owned.Contains(id) || keep.Contains(id)))
            step.JourneyId = null;
        if (step.JourneyId is { } kept)
        {
            keep.Add(kept);
            return;
        }
        if (NeedsLink(step)) step.Status = TripStepStatuses.NotStarted;
    }

    // A moved start or a days cut written by an app without steps: the expectation follows the new trip.
    public static void ClearDates(TripSteps? steps)
    {
        if (steps?.GettingThere != null) steps.GettingThere.Date = null;
        if (steps?.ComingBack != null) steps.ComingBack.Date = null;
    }
}

// LegIndex (city leg) and TravelDay only for a segment.
public sealed record TripJourneyLink(string Role, int? LegIndex, int? TravelDay);

// The plan is the only place that says which travel a journey is; SavedJourneys.TripId only says it belongs to the trip.
public static class TripJourneyLinks
{
    public static TripJourneyLink? Resolve(TripPlan plan, Guid journeyId)
    {
        if (journeyId == Guid.Empty) return null;
        if (plan.Steps?.GettingThere?.JourneyId == journeyId) return new TripJourneyLink(TripStepRoles.GettingThere, null, null);
        if (plan.Steps?.ComingBack?.JourneyId == journeyId) return new TripJourneyLink(TripStepRoles.ComingBack, null, null);
        var legIndex = SegmentLegOf(plan, journeyId);
        return legIndex is { } k ? new TripJourneyLink(TripStepRoles.Segment, k, plan.LegStart(k)) : null;
    }

    // The first leg 2+ whose journey arrival carries the id.
    public static int? SegmentLegOf(TripPlan plan, Guid journeyId)
    {
        if (plan.Legs is not { Count: > 1 }) return null;
        foreach (var leg in plan.Legs)
            if (leg != null && leg.Index >= 2 && TripTravelRules.JourneyIdOf(leg.Arrival) == journeyId) return leg.Index;
        return null;
    }

    // Step ids, then arrival ids; no duplicates.
    public static List<Guid> ReferencedIds(TripPlan plan)
    {
        var ids = TripStepRules.ReferencedIds(plan.Steps);
        foreach (var id in TripTravelRules.ArrivalIds(plan))
            if (!ids.Contains(id)) ids.Add(id);
        return ids;
    }
}

public static class TripTravelRules
{
    public static Guid? JourneyIdOf(TripTravel? arrival)
        => arrival?.Mode == TripTravelModes.Journey && arrival.JourneyId is { } id && id != Guid.Empty ? id : null;

    public static List<Guid> ArrivalIds(TripPlan plan)
    {
        var ids = new List<Guid>();
        if (plan.Legs == null) return ids;
        foreach (var leg in plan.Legs)
            if (leg != null && leg.Index >= 2 && JourneyIdOf(leg.Arrival) is { } id && !ids.Contains(id)) ids.Add(id);
        return ids;
    }

    // Runs after TripStepRules.Sanitise with its keep list: a journey arrival whose id is missing, not owned or already
    // used (by a step or an earlier leg) loses its travel. Own arrivals are untouched. Mutates the plan and keep.
    public static void SanitiseArrivals(TripPlan plan, ISet<Guid> owned, ICollection<Guid> keep)
    {
        if (plan.Legs == null) return;
        foreach (var leg in plan.Legs)
        {
            if (leg == null || leg.Index < 2 || leg.Arrival?.Mode != TripTravelModes.Journey) continue;
            if (leg.Arrival.JourneyId is not { } id || id == Guid.Empty || !owned.Contains(id) || keep.Contains(id))
            {
                leg.Arrival = null;
                continue;
            }
            keep.Add(id);
        }
    }
}
