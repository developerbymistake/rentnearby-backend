using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Models;

public static class TripRowKinds
{
    public const string GettingThere = "gettingThere", Stay = "stay", Between = "between", Taxi = "taxi", ComingBack = "comingBack";
}

// done = own, saved, confirmed, booked (the traveller's word); in progress = asked, offer; skipped rows leave the total.
public static class TripRowStates
{
    public const string Todo = "todo", Asked = "asked", Offer = "offer", Confirmed = "confirmed", Own = "own",
        Saved = "saved", Booked = "booked", Skipped = "skipped", Attention = "attention";

    public static bool IsDone(string state) => state is Own or Saved or Confirmed or Booked;
    public static bool IsInProgress(string state) => state is Asked or Offer;
}

// Wire id: gettingThere | stay:2 | between:2 | taxi | comingBack.
public sealed record TripRowKey(string Kind, int? LegIndex)
{
    public string Id
    {
        get
        {
            var perCity = Kind == TripRowKinds.Stay || Kind == TripRowKinds.Between;
            return perCity && LegIndex is { } k ? $"{Kind}:{k}" : Kind;
        }
    }

    public static TripRowKey? Parse(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var parts = id.Split(':');
        var kind = parts[0];
        if (kind is TripRowKinds.GettingThere or TripRowKinds.Taxi or TripRowKinds.ComingBack)
            return parts.Length == 1 ? new TripRowKey(kind, null) : null;
        if (kind is TripRowKinds.Stay or TripRowKinds.Between)
            return parts.Length == 2 && int.TryParse(parts[1], out var k) && k >= 1 ? new TripRowKey(kind, k) : null;
        return null;
    }
}

public sealed class TripEasyRow
{
    public TripRowKey Key { get; init; } = new(TripRowKinds.Taxi, null);
    public string State { get; init; } = TripRowStates.Todo;
    public bool NeedsYou { get; init; }
    public int AskedCount { get; init; }
    // Stay rows only (asked, offer, booked, confirmed): nights of the leg and how many a live row covers.
    public int NightsTotal { get; init; }
    public int NightsCovered { get; init; }
    public bool Partial => NightsTotal > 0 && NightsCovered < NightsTotal;
    // The rows and journey behind the state (summary detail); empty for a mark or a todo row.
    public IReadOnlyList<TripEnquiry> Enquiries { get; init; } = Array.Empty<TripEnquiry>();
    public SavedJourney? Journey { get; init; }
}

public sealed class TripEasyRowDto
{
    public string Key { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public int? LegIndex { get; set; }
    public string State { get; set; } = TripRowStates.Todo;
    public bool NeedsYou { get; set; }
    public int AskedCount { get; set; }
    public int? NightsTotal { get; set; }
    public int? NightsCovered { get; set; }
}

public sealed class TripNextRow
{
    public string Kind { get; set; } = string.Empty;
    public int? LegIndex { get; set; }
    public string Key { get; set; } = string.Empty;
}

public sealed class TripProgress
{
    // Rows that are not skipped.
    public int Total { get; set; }
    public int Done { get; set; }
    public int InProgress { get; set; }
    public int Attention { get; set; }
    public int Percent { get; set; }
    public bool NeedsYou { get; set; }
    public TripNextRow? Next { get; set; }
    public DateTime? LastEventAt { get; set; }
    public List<TripEasyRowDto> Rows { get; set; } = new();
}

// The Make it easy rows (CONTRACT_V2 1.5); the app's TripEasyRules implements the same table.
// Order: gettingThere, per city (between:k from city 2, then stay:k), taxi, comingBack.
public static class TripEasyRows
{
    private static readonly string[] Problem =
    [
        TripEnquiryStatuses.NotAvailable, TripEnquiryStatuses.Expired, TripEnquiryStatuses.Failed, TripEnquiryStatuses.CancelledByOwner,
    ];

    // revisedPending = ids of stay rows with a pending revised price (it needs the traveller).
    public static List<TripEasyRow> Build(Trip trip, TripPlan plan, IReadOnlyList<TripEnquiry> enquiries, IReadOnlyList<SavedJourney>? journeys,
        IReadOnlySet<Guid>? revisedPending = null)
    {
        var ctx = new Context(plan, trip.StartDate ?? plan.Settings?.StartDate, enquiries, journeys ?? Array.Empty<SavedJourney>(), revisedPending);
        var rows = new List<TripEasyRow> { JourneyRow(ctx, TripStepRoles.GettingThere) };

        var legCount = Math.Max(1, plan.Legs?.Count ?? 1);
        for (var k = 1; k <= legCount; k++)
        {
            if (k >= 2) rows.Add(BetweenRow(ctx, k));
            rows.Add(StayRow(ctx, k));
        }

        rows.Add(TaxiRow(ctx));
        rows.Add(JourneyRow(ctx, TripStepRoles.ComingBack));
        return rows;
    }

    public static bool IsRowDone(TripEasyRow r) => TripRowStates.IsDone(r.State) && !r.Partial;

    public static TripProgress Progress(IReadOnlyList<TripEasyRow> rows, DateTime? lastEventAt = null)
    {
        var counted = rows.Where(r => r.State != TripRowStates.Skipped).ToList();
        var done = counted.Count(IsRowDone);
        var next = rows.FirstOrDefault(r => r.State == TripRowStates.Attention || r.NeedsYou)
                   ?? rows.FirstOrDefault(r => r.State == TripRowStates.Todo)
                   ?? rows.FirstOrDefault(r => TripRowStates.IsInProgress(r.State));
        return new TripProgress
        {
            Total = counted.Count,
            Done = done,
            InProgress = counted.Count(r => TripRowStates.IsInProgress(r.State)),
            Attention = counted.Count(r => r.State == TripRowStates.Attention),
            Percent = counted.Count == 0 ? 100 : done * 100 / counted.Count,
            NeedsYou = rows.Any(r => r.NeedsYou),
            Next = next == null ? null : new TripNextRow { Kind = next.Key.Kind, LegIndex = next.Key.LegIndex, Key = next.Key.Id },
            LastEventAt = lastEventAt,
            Rows = rows.Select(ToDto).ToList(),
        };
    }

    public static TripProgress Compute(Trip trip, TripPlan plan, IReadOnlyList<TripEnquiry> enquiries, IReadOnlyList<SavedJourney>? journeys,
        IReadOnlySet<Guid>? revisedPending = null)
        => Progress(Build(trip, plan, enquiries, journeys, revisedPending), enquiries.Count == 0 ? null : enquiries.Max(e => e.UpdatedAt));

    public static TripEasyRowDto ToDto(TripEasyRow row) => new()
    {
        Key = row.Key.Id,
        Kind = row.Key.Kind,
        LegIndex = row.Key.LegIndex,
        State = row.State,
        NeedsYou = row.NeedsYou,
        AskedCount = row.AskedCount,
        NightsTotal = row.NightsTotal > 0 ? row.NightsTotal : null,
        NightsCovered = row.NightsTotal > 0 ? row.NightsCovered : null,
    };

    // The booked rows' money, each row once.
    public static int AgreedTotal(IReadOnlyList<TripEnquiry> enquiries)
        => enquiries
            .Where(e => TripEnquiryStatuses.IsBooked(e.Status))
            .GroupBy(e => e.Id)
            .Select(g => g.First())
            .Sum(e => (e.Kind == TripEnquiryKinds.Taxi ? e.AgreedFare : e.AgreedTotal) ?? 0);

    public static string Title(TripEasyRow row, TripPlan plan) => Title(row.Key, plan);

    public static string Title(TripRowKey key, TripPlan plan)
    {
        var legs = plan.Legs ?? new List<TripCityLeg>();
        string City(int k) => k >= 1 && k <= legs.Count && !string.IsNullOrWhiteSpace(legs[k - 1]?.CityName) ? legs[k - 1].CityName : plan.CityName ?? string.Empty;
        return key.Kind switch
        {
            TripRowKinds.GettingThere => "Getting there",
            TripRowKinds.ComingBack => "Coming back",
            TripRowKinds.Taxi => "Local taxi",
            TripRowKinds.Stay => $"Stay in {City(key.LegIndex ?? 1)}".TrimEnd(),
            TripRowKinds.Between => $"{City((key.LegIndex ?? 2) - 1)} → {City(key.LegIndex ?? 2)}",
            _ => key.Id,
        };
    }

    private sealed class Context(
        TripPlan plan, DateOnly? start, IReadOnlyList<TripEnquiry> enquiries, IReadOnlyList<SavedJourney> journeys, IReadOnlySet<Guid>? revisedPending)
    {
        public IReadOnlySet<Guid> RevisedPending { get; } = revisedPending ?? new HashSet<Guid>();
        public TripPlan Plan { get; } = plan;
        public DateOnly? Start { get; } = start;
        public IReadOnlyList<TripEnquiry> Enquiries { get; } = enquiries;
        public IReadOnlyList<SavedJourney> Journeys { get; } = journeys;
        public HashSet<Guid> Dismissed { get; } = (plan.DismissedEnquiryIds ?? new List<Guid>()).ToHashSet();
        public List<TripEnquiry> Local { get; } = enquiries.Where(e => e.Status != TripEnquiryStatuses.Closed && !TripSegmentRows.IsSegment(e)).ToList();
        public int DayCount { get; } = plan.Days?.Count ?? 0;
    }

    private static bool Marked(TripRowMark? mark, string status) => mark?.Status == status;

    private static TripEasyRow Row(string kind, int? leg, string state, bool needsYou = false, int asked = 0,
        IEnumerable<TripEnquiry>? rows = null, SavedJourney? journey = null, int nightsTotal = 0, int nightsCovered = 0)
        => new()
        {
            Key = new TripRowKey(kind, leg),
            State = state,
            NeedsYou = needsYou,
            AskedCount = asked,
            Enquiries = rows?.ToList() ?? new List<TripEnquiry>(),
            Journey = journey,
            NightsTotal = nightsTotal,
            NightsCovered = nightsCovered,
        };

    private static List<TripEnquiry> Newest(IEnumerable<TripEnquiry> rows) => rows.OrderByDescending(e => e.UpdatedAt).ToList();

    private static int AskedOf(IEnumerable<TripEnquiry> live)
        => live.Count(e => e.Status is TripEnquiryStatuses.Asked or TripEnquiryStatuses.Offered
            or TripEnquiryStatuses.Countered or TripEnquiryStatuses.FareSent);

    // Only the newest closed row the traveller has not dismissed counts; his own decline or cancel is never a problem.
    private static bool LastClosedIsProblem(Context ctx, List<TripEnquiry> newestFirst)
    {
        var last = newestFirst.FirstOrDefault(e =>
            !TripEnquiryStatuses.Open.Contains(e.Status) && e.Status != TripEnquiryStatuses.Closed && !ctx.Dismissed.Contains(e.Id));
        return last != null && Problem.Contains(last.Status);
    }

    private static TripEasyRow StayRow(Context ctx, int k)
    {
        var legs = ctx.Plan.Legs ?? new List<TripCityLeg>();
        var mark = k >= 1 && k <= legs.Count ? legs[k - 1]?.StayMark : null;
        if (Marked(mark, TripRowMarkStatuses.Own)) return Row(TripRowKinds.Stay, k, TripRowStates.Own);
        if (Marked(mark, TripRowMarkStatuses.Skipped)) return Row(TripRowKinds.Stay, k, TripRowStates.Skipped);

        var days = ctx.Plan.DaysOf(k).Select(d => d.Day).ToHashSet();
        var multi = (ctx.Plan.Legs?.Count ?? 0) > 1;
        var covering = new List<TripEnquiry>();
        var moved = new List<TripEnquiry>();
        foreach (var e in ctx.Local.Where(e => e.Kind == TripEnquiryKinds.Stay))
        {
            if (!TripBookingMap.DaysOf(e, ctx.Start, ctx.DayCount).Any(days.Contains)) continue;
            var home = TripLegRules.HomeLegIndex(e, ctx.Plan);
            var elsewhere = home is { } h && (multi || h == TripLegRules.RemovedCityLeg) && h != k;
            (elsewhere ? moved : covering).Add(e);
        }

        covering = Newest(covering);
        var live = covering.Where(e => TripEnquiryStatuses.Open.Contains(e.Status)).ToList();
        bool Has(params string[] statuses) => live.Any(e => statuses.Contains(e.Status));
        var asked = AskedOf(live);
        var covered = live.SelectMany(e => TripBookingMap.DaysOf(e, ctx.Start, ctx.DayCount)).Where(days.Contains).Distinct().Count();
        TripEasyRow Cover(string state, bool needsYou) => Row(TripRowKinds.Stay, k, state, needsYou, asked, live, null, days.Count, covered);

        var both = live.FirstOrDefault(e => e.Status == TripEnquiryStatuses.ConfirmedByBoth);
        if (both == null && moved.Any(e => e.Status == TripEnquiryStatuses.ConfirmedByBoth))
            return Row(TripRowKinds.Stay, k, TripRowStates.Attention, true, 0, moved);
        if (both != null)
        {
            var replace = live.Any(e => e.Id != both.Id && e.Status == TripEnquiryStatuses.Offered);
            var needsYou = both.AdvanceStatus == TripAdvanceStatuses.Pending || ctx.RevisedPending.Contains(both.Id) || replace;
            return Cover(TripRowStates.Confirmed, needsYou);
        }
        if (Has(TripEnquiryStatuses.Offered))
            return Cover(TripRowStates.Offer, true);
        if (Has(TripEnquiryStatuses.Asked, TripEnquiryStatuses.Countered))
            return Cover(TripRowStates.Asked, false);
        if (LastClosedIsProblem(ctx, covering)) return Row(TripRowKinds.Stay, k, TripRowStates.Attention, false, 0, covering);
        return Row(TripRowKinds.Stay, k, TripRowStates.Todo);
    }

    private static TripEasyRow TaxiRow(Context ctx)
    {
        var mark = ctx.Plan.TaxiMark;
        if (Marked(mark, TripRowMarkStatuses.Own)) return Row(TripRowKinds.Taxi, null, TripRowStates.Own);
        if (Marked(mark, TripRowMarkStatuses.Skipped)) return Row(TripRowKinds.Taxi, null, TripRowStates.Skipped);

        var covering = Newest(ctx.Local.Where(e => e.Kind == TripEnquiryKinds.Taxi));
        var live = covering.Where(e => TripEnquiryStatuses.Open.Contains(e.Status)).ToList();
        bool Has(params string[] statuses) => live.Any(e => statuses.Contains(e.Status));
        var asked = AskedOf(live);

        var both = live.FirstOrDefault(e => e.Status == TripEnquiryStatuses.ConfirmedByBoth);
        if (both != null)
            return Row(TripRowKinds.Taxi, null, TripRowStates.Confirmed, live.Any(e => e.Id != both.Id && e.Status == TripEnquiryStatuses.FareSent), asked, live);
        if (Has(TripEnquiryStatuses.BookedByTraveller))
            return Row(TripRowKinds.Taxi, null, TripRowStates.Booked, Has(TripEnquiryStatuses.FareSent), asked, live);
        if (Has(TripEnquiryStatuses.FareSent)) return Row(TripRowKinds.Taxi, null, TripRowStates.Offer, true, asked, live);
        if (Has(TripEnquiryStatuses.Asked))
            return Row(TripRowKinds.Taxi, null, TripRowStates.Asked, false, asked, live);
        if (LastClosedIsProblem(ctx, covering)) return Row(TripRowKinds.Taxi, null, TripRowStates.Attention, false, 0, covering);
        return Row(TripRowKinds.Taxi, null, TripRowStates.Todo);
    }

    private static TripEasyRow JourneyRow(Context ctx, string role)
    {
        var kind = role == TripStepRoles.ComingBack ? TripRowKinds.ComingBack : TripRowKinds.GettingThere;
        var step = TripStepRules.StepOf(ctx.Plan.Steps, role);
        var status = TripStepRules.StatusOf(step);
        if (status == TripStepStatuses.Own) return Row(kind, null, TripRowStates.Own);
        if (status == TripStepStatuses.Skipped) return Row(kind, null, TripRowStates.Skipped);

        var journeyId = TripStepRules.NeedsLink(step) ? step!.JourneyId : null;
        var journey = JourneyOf(ctx, journeyId);
        var rows = RowsOf(ctx, journeyId);
        var travel = TripTravelState.OfStep(step, journey, rows, TripStepDates.Expected(role, ctx.Plan, null));
        if (journey != null && travel != TripTravelStates.Own
            && (travel != TripTravelStates.Confirmed ? StartMoved(ctx.Plan, role, journey) : CityMoved(ctx.Plan, role, journey)))
            return Row(kind, null, TripRowStates.Attention, false, 0, rows, journey);
        var (state, needsYou) = FromTravel(travel, AnyCarNeedsYou(journey, rows, step?.OwnCars));
        return Row(kind, null, state, needsYou, AskedCars(rows), rows, journey);
    }

    private static TripEasyRow BetweenRow(Context ctx, int k)
    {
        var leg = TripStepDates.LegFor(TripStepRoles.Segment, ctx.Plan, k);
        if (Marked(leg?.TravelMark, TripRowMarkStatuses.Own)) return Row(TripRowKinds.Between, k, TripRowStates.Own);
        if (Marked(leg?.TravelMark, TripRowMarkStatuses.Skipped)) return Row(TripRowKinds.Between, k, TripRowStates.Skipped);

        var arrival = leg?.Arrival;
        var journeyId = TripTravelRules.JourneyIdOf(arrival);
        var journey = JourneyOf(ctx, journeyId);
        if (arrival != null && journey == null) return Row(TripRowKinds.Between, k, TripRowStates.Asked, false, 0, RowsOf(ctx, journeyId));
        var rows = RowsOf(ctx, journeyId);
        var travel = TripTravelState.Of(arrival, journey, rows, TripStepDates.Expected(TripStepRoles.Segment, ctx.Plan, k));
        var (state, needsYou) = FromTravel(travel, AnyCarNeedsYou(journey, rows, arrival?.OwnCars));
        return Row(TripRowKinds.Between, k, state, needsYou, AskedCars(rows), rows, journey);
    }

    private static int AskedCars(IEnumerable<TripEnquiry> rows)
        => rows.Where(r => r.Status is TripEnquiryStatuses.Asked or TripEnquiryStatuses.FareSent)
            .Select(r => TripSegmentRows.CarOf(r) ?? 0).Distinct().Count();

    // Same rule as the app's StartChanged: Starting from or the trip city moved away from the journey ends.
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
        TripTravelStates.Own => (TripRowStates.Own, false),
        TripTravelStates.Confirmed => (TripRowStates.Confirmed, false),
        TripTravelStates.Saved => (TripRowStates.Saved, false),
        TripTravelStates.Free => (TripRowStates.Offer, true),
        TripTravelStates.Booked => (TripRowStates.Booked, false),
        TripTravelStates.Asked or TripTravelStates.PartlyBooked => (TripRowStates.Asked, carNeedsYou),
        TripTravelStates.CarMissing or TripTravelStates.DriverUnavailable or TripTravelStates.DateChanged => (TripRowStates.Attention, false),
        _ => (TripRowStates.Todo, false),
    };
}
