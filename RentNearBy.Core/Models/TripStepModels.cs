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

    public static bool DateChanged(DateOnly? expected, DateOnly travel) => expected is { } e && e != travel;

    public static bool StartChanged(TripStartFrom? start, double lat, double lon)
        => start != null && GeoMath.HaversineKm(start.Lat, start.Lon, lat, lon) * 1000 > SameCityMetres;

    // Getting there leaves from Starting from; Coming back ends at Going home to, else there.
    public static TripStartFrom? EndOf(string role, TripSteps? steps)
        => role == TripStepRoles.ComingBack ? steps?.GoingHomeTo ?? steps?.StartFrom : steps?.StartFrom;


    public static bool StartChanged(string role, TripStartFrom? start, SavedJourney j)
        => role == TripStepRoles.ComingBack
            ? StartChanged(start, (double)j.ToLatitude, (double)j.ToLongitude)
            : StartChanged(start, (double)j.FromLatitude, (double)j.FromLongitude);

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
}

// The one date rule of every journey row (mirror of the app's TripStepRules.expectedDate).
public static class TripStepDates
{
    // Getting there: own date, else the start. Coming back: own date, else ComingBackDate (leave-on). Segment: the leg's
    // first day. Null without a start date (an own step date still counts).
    public static DateOnly? Expected(string role, TripPlan plan, int? legIndex)
    {
        if (role == TripStepRoles.Segment)
        {
            var day = legIndex is { } k ? plan.LegStart(k) : 0;
            return day > 0 && plan.Settings?.StartDate is { } start ? TripExecutionRules.NightDate(start, day) : null;
        }
        if (TripStepRules.StepOf(plan.Steps, role)?.Date is { } own) return own;
        return role == TripStepRoles.ComingBack ? plan.ComingBackDate : plan.Settings?.StartDate;
    }

    // The city a journey row belongs to: the first city for Getting there, the last for Coming back, else the leg entered.
    public static TripCityLeg? LegFor(string role, TripPlan plan, int? legIndex)
    {
        if (plan.Legs is not { Count: > 0 } legs) return null;
        return role switch
        {
            TripStepRoles.GettingThere => legs[0],
            TripStepRoles.ComingBack => legs[^1],
            _ => legIndex is { } k && k >= 1 && k <= legs.Count ? legs[k - 1] : null,
        };
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
        => arrival?.JourneyId is { } id && id != Guid.Empty ? id : null;

    public static List<Guid> ArrivalIds(TripPlan plan)
    {
        var ids = new List<Guid>();
        if (plan.Legs == null) return ids;
        foreach (var leg in plan.Legs)
            if (leg != null && leg.Index >= 2 && JourneyIdOf(leg.Arrival) is { } id && !ids.Contains(id)) ids.Add(id);
        return ids;
    }

    // Runs after TripStepRules.Sanitise with its keep list: a journey arrival whose id is missing, not owned or already
    // used (by a step or an earlier leg) loses its travel. Mutates the plan and keep.
    public static void SanitiseArrivals(TripPlan plan, ISet<Guid> owned, ICollection<Guid> keep)
    {
        if (plan.Legs == null) return;
        foreach (var leg in plan.Legs)
        {
            if (leg == null || leg.Index < 2 || leg.Arrival == null) continue;
            if (leg.Arrival.JourneyId is not { } id || id == Guid.Empty || !owned.Contains(id) || keep.Contains(id))
            {
                leg.Arrival = null;
                continue;
            }
            keep.Add(id);
        }
    }
}
