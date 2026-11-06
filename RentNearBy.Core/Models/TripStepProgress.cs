using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Models;

public static class TripStepKinds
{
    public const string Basics = "basics", GettingThere = "gettingThere", Places = "places", Taxi = "taxi", Stay = "stay",
        Travel = "travel", ComingBack = "comingBack";
}

// done, own and skipped count toward the percentage; todo, waiting and attention do not.
public static class TripStepStates
{
    public const string Todo = "todo", Waiting = "waiting", Attention = "attention", Done = "done", Own = "own", Skipped = "skipped";

    public static bool IsCounted(string state) => state is Done or Own or Skipped;
}

public sealed record TripStepInfo(string Kind, string Key, int? Day, int? LegIndex, string State, bool NeedsYou)
{
    public bool Counted => TripStepStates.IsCounted(State);
    public bool Decided => Counted && Kind is not (TripStepKinds.Basics or TripStepKinds.Places);
}

public sealed class TripNextStep
{
    public string Kind { get; set; } = string.Empty;
    public int? Day { get; set; }
    public int? LegIndex { get; set; }
    public string Key { get; set; } = string.Empty;
}

public sealed class TripProgress
{
    public int Total { get; set; }
    public int Counted { get; set; }
    // Counted steps other than basics and places: 0 = no stay, taxi or journey sorted yet.
    public int Decided { get; set; }
    public int Waiting { get; set; }
    // Waiting while a host or driver acts (waiting and not needsYou).
    public int WaitingForReply { get; set; }
    // Steps that need the traveller, in any state.
    public int NeedYou { get; set; }
    public int Attention { get; set; }
    public int Percent { get; set; }
    public TripNextStep? Next { get; set; }
    public bool NeedsYou { get; set; }
    public DateTime? LastEventAt { get; set; }
}

// The confirm-based step model (CONTRACT_WIZARD 1.2, 1.3, 2); the app's TripWizardRules implements the same table.
// Stay done = ConfirmedByBoth; taxi done = ConfirmedByBoth; journey done = every car ConfirmedByBoth or his own.
// The server never marks a step as not needed.
public static class TripStepProgress
{
    private static readonly string[] Problem =
    [
        TripEnquiryStatuses.NotAvailable, TripEnquiryStatuses.Expired, TripEnquiryStatuses.Failed, TripEnquiryStatuses.CancelledByOwner,
    ];

    // revisedPending = ids of stay rows with a pending revised price (it needs the traveller).
    public static TripProgress Compute(Trip trip, TripPlan plan, IReadOnlyList<TripEnquiry> enquiries, IReadOnlyList<SavedJourney> journeys,
        IReadOnlySet<Guid>? revisedPending = null)
        => Summarise(Steps(trip, plan, enquiries, journeys, revisedPending), enquiries.Count == 0 ? null : enquiries.Max(e => e.UpdatedAt));

    public static TripProgress Summarise(IReadOnlyList<TripStepInfo> steps, DateTime? lastEventAt = null)
    {
        var counted = steps.Count(s => s.Counted);
        var waiting = steps.Count(s => s.State == TripStepStates.Waiting);
        var needYou = steps.Count(s => s.NeedsYou);
        var next = steps.FirstOrDefault(s => s.State == TripStepStates.Attention || s.NeedsYou)
                   ?? steps.FirstOrDefault(s => s.State == TripStepStates.Todo)
                   ?? steps.FirstOrDefault(s => s.State == TripStepStates.Waiting);
        return new TripProgress
        {
            Total = steps.Count,
            Counted = counted,
            Decided = steps.Count(s => s.Decided),
            Waiting = waiting,
            WaitingForReply = steps.Count(s => s.State == TripStepStates.Waiting && !s.NeedsYou),
            NeedYou = needYou,
            Attention = steps.Count(s => s.State == TripStepStates.Attention),
            Percent = steps.Count == 0 ? 0 : counted * 100 / steps.Count,
            Next = next == null ? null : new TripNextStep { Kind = next.Kind, Day = next.Day, LegIndex = next.LegIndex, Key = next.Key },
            NeedsYou = needYou > 0,
            LastEventAt = lastEventAt,
        };
    }

    // List order: basics, Getting there, per city (city change first from city 2) places / taxi / stay per day, Coming back.
    public static List<TripStepInfo> Steps(Trip trip, TripPlan plan, IReadOnlyList<TripEnquiry> enquiries, IReadOnlyList<SavedJourney> journeys,
        IReadOnlySet<Guid>? revisedPending = null)
    {
        var ctx = new Context(plan, trip.StartDate ?? plan.Settings?.StartDate, enquiries, journeys, revisedPending);
        var steps = new List<TripStepInfo>
        {
            new(TripStepKinds.Basics, TripStepKinds.Basics, null, null, BasicsState(plan), false),
            JourneyStep(ctx, TripStepRoles.GettingThere),
        };

        var legCount = Math.Max(1, plan.Legs?.Count ?? 1);
        for (var k = 1; k <= legCount; k++)
        {
            if (k >= 2)
            {
                var (state, needsYou) = TravelState(ctx, k);
                steps.Add(new(TripStepKinds.Travel, $"travel:{k}", null, k, state, needsYou));
            }
            var i = 0;
            foreach (var day in plan.DaysOf(k).OrderBy(d => d.Day))
            {
                i++;
                steps.Add(new(TripStepKinds.Places, $"places:{k}:{i}", day.Day, k, PlacesState(day), false));
                var (taxi, taxiNeedsYou) = TaxiState(ctx, day);
                steps.Add(new(TripStepKinds.Taxi, $"taxi:{k}:{i}", day.Day, k, taxi, taxiNeedsYou));
                var (stay, stayNeedsYou) = StayState(ctx, day);
                steps.Add(new(TripStepKinds.Stay, $"stay:{k}:{i}", day.Day, k, stay, stayNeedsYou));
            }
        }

        steps.Add(JourneyStep(ctx, TripStepRoles.ComingBack));
        return steps;
    }

    private sealed class Context(
        TripPlan plan, DateOnly? start, IReadOnlyList<TripEnquiry> enquiries, IReadOnlyList<SavedJourney> journeys, IReadOnlySet<Guid>? revisedPending)
    {
        public IReadOnlySet<Guid> RevisedPending { get; } = revisedPending ?? new HashSet<Guid>();
        public TripPlan Plan { get; } = plan;
        public IReadOnlyList<TripEnquiry> Enquiries { get; } = enquiries;
        public IReadOnlyList<SavedJourney> Journeys { get; } = journeys;
        public HashSet<Guid> Dismissed { get; } = (plan.DismissedEnquiryIds ?? new List<Guid>()).ToHashSet();
        public List<(TripEnquiry Row, List<int> Days, List<int> Moved)> Stays { get; } = Rows(enquiries, plan, start, TripEnquiryKinds.Stay);
        public List<(TripEnquiry Row, List<int> Days, List<int> Moved)> Taxis { get; } = Rows(enquiries, plan, start, TripEnquiryKinds.Taxi);

        // Days in the row's own city count for the step; days now in another city (dates changed) are Moved.
        private static List<(TripEnquiry, List<int>, List<int>)> Rows(IReadOnlyList<TripEnquiry> rows, TripPlan plan, DateOnly? start, string kind)
            => rows
                .Where(e => e.Kind == kind && !TripSegmentRows.IsSegment(e))
                .Select(e => Split(e, TripBookingMap.DaysOf(e, start, plan.Days.Count), plan))
                .ToList();

        private static (TripEnquiry, List<int>, List<int>) Split(TripEnquiry e, List<int> days, TripPlan plan)
        {
            if (TripLegRules.HomeLegIndex(e, plan) is not { } home || ((plan.Legs?.Count ?? 0) <= 1 && home != TripLegRules.RemovedCityLeg))
                return (e, days, new List<int>());
            return (e, days.Where(d => plan.LegIndexOf(d) == home).ToList(), days.Where(d => plan.LegIndexOf(d) != home).ToList());
        }
    }

    public static string BasicsState(TripPlan plan)
        => plan.Settings?.StartDate != null && plan.Settings.Adults != null && plan.Steps?.StartFrom != null
            ? TripStepStates.Done
            : TripStepStates.Todo;

    public static string PlacesState(TripDay day)
    {
        if (day.RestDay == true) return TripStepStates.Skipped;
        return day.Spots is { Count: > 0 } ? TripStepStates.Done : TripStepStates.Todo;
    }

    private static (string State, bool NeedsYou) StayState(Context ctx, TripDay day)
    {
        var d = day.Day;
        var stay = day.Stay;
        var covering = Newest(ctx.Stays.Where(x => x.Days.Contains(d)).Select(x => x.Row));
        var live = covering.Where(e => TripEnquiryStatuses.Open.Contains(e.Status)).ToList();
        bool Has(params string[] statuses) => live.Any(e => statuses.Contains(e.Status));

        if (MovedHere(ctx.Stays, d) && !Has(TripEnquiryStatuses.ConfirmedByBoth)) return (TripStepStates.Attention, true);

        if ((stay?.Skipped == true || stay?.IsOwn == true) && Has(TripEnquiryStatuses.Confirmed))
            return (TripStepStates.Attention, true);
        if (stay?.Skipped == true) return (TripStepStates.Skipped, false);
        if (stay?.IsOwn == true) return (TripStepStates.Own, false);

        var both = live.FirstOrDefault(e => e.Status == TripEnquiryStatuses.ConfirmedByBoth);
        if (both != null)
        {
            var offerToReplace = live.Any(e => e.Id != both.Id && e.Status == TripEnquiryStatuses.Offered);
            return (TripStepStates.Done,
                both.AdvanceStatus == TripAdvanceStatuses.Pending || ctx.RevisedPending.Contains(both.Id) || offerToReplace);
        }
        if (live.Count > 0)
        {
            if (Has(TripEnquiryStatuses.ConfirmedByTraveller)) return (TripStepStates.Waiting, false);
            return (TripStepStates.Waiting, Has(TripEnquiryStatuses.Offered));
        }
        if (stay?.SameAsDay != null) return (TripStepStates.Waiting, true);
        if (stay?.StayId is { } id && id != Guid.Empty) return (TripStepStates.Waiting, true);
        if ((day.StayShortlist ?? new List<TripStayShortlistItem>()).Any(s => covering.All(e => e.ListingId != s.StayId)))
            return (TripStepStates.Waiting, true);
        if (LastClosedIsProblem(ctx, covering)) return (TripStepStates.Attention, false);
        return (TripStepStates.Todo, false);
    }

    private static (string State, bool NeedsYou) TaxiState(Context ctx, TripDay day)
    {
        var d = day.Day;
        var taxi = day.Taxi;
        var covering = Newest(ctx.Taxis.Where(x => x.Days.Contains(d)).Select(x => x.Row));
        var live = covering.Where(e => TripEnquiryStatuses.Open.Contains(e.Status)).ToList();
        bool Has(params string[] statuses) => live.Any(e => statuses.Contains(e.Status));

        if (MovedHere(ctx.Taxis, d) && !Has(TripEnquiryStatuses.ConfirmedByBoth)) return (TripStepStates.Attention, true);

        if ((taxi?.Skipped == true || taxi?.IsOwn == true)
            && Has(TripEnquiryStatuses.BookedByTraveller, TripEnquiryStatuses.ConfirmedByBoth))
            return (TripStepStates.Attention, true);
        if (taxi?.Skipped == true) return (TripStepStates.Skipped, false);
        if (taxi?.IsOwn == true) return (TripStepStates.Own, false);

        var bothTaxi = live.FirstOrDefault(e => e.Status == TripEnquiryStatuses.ConfirmedByBoth);
        if (bothTaxi != null)
            return (TripStepStates.Done, live.Any(e => e.Id != bothTaxi.Id && e.Status == TripEnquiryStatuses.FareSent));
        if (live.Count > 0)
        {
            if (Has(TripEnquiryStatuses.BookedByTraveller)) return (TripStepStates.Waiting, false);
            return (TripStepStates.Waiting, Has(TripEnquiryStatuses.FareSent));
        }
        if ((taxi?.TaxiId is { } id && id != Guid.Empty) || taxi?.Booking != null) return (TripStepStates.Waiting, true);
        if ((day.TaxiShortlist ?? new List<TripTaxiShortlistItem>()).Any(s => covering.All(e => e.ListingId != s.TaxiId)))
            return (TripStepStates.Waiting, true);
        if (LastClosedIsProblem(ctx, covering)) return (TripStepStates.Attention, false);
        return (TripStepStates.Todo, false);
    }

    // A live booking of another city whose dates now land on this day.
    private static bool MovedHere(List<(TripEnquiry Row, List<int> Days, List<int> Moved)> rows, int day)
        => rows.Any(x => x.Moved.Contains(day) && TripEnquiryStatuses.Booked.Contains(x.Row.Status));

    private static List<TripEnquiry> Newest(IEnumerable<TripEnquiry> rows) => rows.OrderByDescending(e => e.UpdatedAt).ToList();

    // Only the newest closed row the traveller has not dismissed counts; his own decline or cancel is never a problem.
    private static bool LastClosedIsProblem(Context ctx, List<TripEnquiry> newestFirst)
    {
        var last = newestFirst.FirstOrDefault(e =>
            !TripEnquiryStatuses.Open.Contains(e.Status) && e.Status != TripEnquiryStatuses.Closed && !ctx.Dismissed.Contains(e.Id));
        return last != null && Problem.Contains(last.Status);
    }

    private static TripStepInfo JourneyStep(Context ctx, string role)
    {
        var step = TripStepRules.StepOf(ctx.Plan.Steps, role);
        var kind = role == TripStepRoles.ComingBack ? TripStepKinds.ComingBack : TripStepKinds.GettingThere;
        if (TripStepRules.StatusOf(step) == TripStepStatuses.Skipped) return new(kind, kind, null, null, TripStepStates.Skipped, false);

        var journeyId = TripStepRules.NeedsLink(step) ? step!.JourneyId : null;
        var journey = JourneyOf(ctx, journeyId);
        var rows = RowsOf(ctx, journeyId);
        var travel = TripTravelState.OfStep(step, journey, rows, TripStepDates.Expected(role, ctx.Plan, null));
        if (journey != null && travel != TripTravelStates.Own
            && (travel != TripTravelStates.Confirmed ? StartMoved(ctx.Plan, role, journey) : CityMoved(ctx.Plan, role, journey)))
            return new(kind, kind, null, null, TripStepStates.Attention, false);
        var (state, needsYou) = FromTravel(travel, AnyCarNeedsYou(journey, rows, step?.OwnCars));
        return new(kind, kind, null, null, state, needsYou);
    }

    private static (string State, bool NeedsYou) TravelState(Context ctx, int legIndex)
    {
        var arrival = TripStepDates.LegFor(TripStepRoles.Segment, ctx.Plan, legIndex)?.Arrival;
        var journeyId = TripTravelRules.JourneyIdOf(arrival);
        var journey = JourneyOf(ctx, journeyId);
        if (arrival?.Mode == TripTravelModes.Journey && journey == null) return (TripStepStates.Waiting, true);
        var rows = RowsOf(ctx, journeyId);
        var travel = TripTravelState.Of(arrival, journey, rows, TripStepDates.Expected(TripStepRoles.Segment, ctx.Plan, legIndex));
        return FromTravel(travel, AnyCarNeedsYou(journey, rows, arrival?.OwnCars));
    }

    // Same rule as the app's TripStepRules.startChanged: Starting from or the trip city moved away from the journey ends.
    private static bool StartMoved(TripPlan plan, string role, SavedJourney j)
    {
        var (endLat, endLon) = role == TripStepRoles.ComingBack
            ? ((double)j.ToLatitude, (double)j.ToLongitude)
            : ((double)j.FromLatitude, (double)j.FromLongitude);
        if (TripStepRules.EndOf(role, plan.Steps) is { } start && !(endLat == 0 && endLon == 0) && TripStepRules.StartChanged(role, start, j)) return true;
        return CityMoved(plan, role, j);
    }

    private static bool CityMoved(TripPlan plan, string role, SavedJourney j)
    {
        var legs = plan.Legs ?? new List<TripCityLeg>();
        var city = role == TripStepRoles.ComingBack ? legs.OrderBy(l => l.Index).LastOrDefault() : legs.FirstOrDefault(l => l.Index == 1);
        if (city == null || (city.Latitude == 0 && city.Longitude == 0)) return false;
        var (lat, lon) = role == TripStepRoles.ComingBack
            ? ((double)j.FromLatitude, (double)j.FromLongitude)
            : ((double)j.ToLatitude, (double)j.ToLongitude);
        if (lat == 0 && lon == 0) return false;
        return GeoMath.HaversineKm(city.Latitude, city.Longitude, lat, lon) * 1000 > TripStepRules.SameCityMetres;
    }

    // An open car the traveller can act on: the driver said free or sent a fare, or nobody was asked yet.
    private static bool AnyCarNeedsYou(SavedJourney? j, List<TripEnquiry> rows, IReadOnlyCollection<int>? ownCars)
    {
        if (j == null) return false;
        var own = ownCars ?? Array.Empty<int>();
        foreach (var car in j.Legs.Where(c => !own.Contains(c.LegIndex)))
        {
            var mine = rows.Where(r => r.Status != TripEnquiryStatuses.Closed && TripSegmentRows.CarOf(r) == car.LegIndex).ToList();
            if (mine.Any(r => r.Status is TripEnquiryStatuses.BookedByTraveller or TripEnquiryStatuses.ConfirmedByBoth)) continue;
            if (car.LastKnownStatus == JourneyLegStatuses.NotFree || !car.DriverAvailable) continue;
            if (car.LastKnownStatus == JourneyLegStatuses.Free || mine.Any(r => r.Status == TripEnquiryStatuses.FareSent)) return true;
            if (car.LastEnquiryId == null && mine.All(r => r.Status != TripEnquiryStatuses.Asked)) return true;
        }
        return false;
    }

    private static SavedJourney? JourneyOf(Context ctx, Guid? id)
        => id is { } j ? ctx.Journeys.FirstOrDefault(x => x.Id == j) : null;

    private static List<TripEnquiry> RowsOf(Context ctx, Guid? id)
        => id is { } j ? TripSegmentRows.OfJourney(ctx.Enquiries, j) : new List<TripEnquiry>();

    public static (string State, bool NeedsYou) FromTravel(string travelState, bool carNeedsYou = false) => travelState switch
    {
        TripTravelStates.Own => (TripStepStates.Own, false),
        TripTravelStates.Confirmed => (TripStepStates.Done, false),
        TripTravelStates.Saved or TripTravelStates.Free => (TripStepStates.Waiting, true),
        TripTravelStates.Asked or TripTravelStates.PartlyBooked or TripTravelStates.Booked => (TripStepStates.Waiting, carNeedsYou),
        TripTravelStates.CarMissing or TripTravelStates.DriverUnavailable or TripTravelStates.DateChanged => (TripStepStates.Attention, false),
        _ => (TripStepStates.Todo, false),
    };
}
