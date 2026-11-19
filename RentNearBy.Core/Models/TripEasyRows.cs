using RentNearBy.Core.DTOs.TaxiRequests;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Models.TaxiRequests;

namespace RentNearBy.Core.Models;

public static class TripRowKinds
{
    public const string GettingThere = "gettingThere", Stay = "stay", Between = "between", Taxi = "taxi", ComingBack = "comingBack";
}

// done = own, confirmed, booked (the traveller's word); a saved plan is not done; in progress = asked, offer; skipped rows leave the total.
public static class TripRowStates
{
    public const string Todo = "todo", Asked = "asked", Offer = "offer", Confirmed = "confirmed", Own = "own",
        Saved = "saved", Booked = "booked", Skipped = "skipped", Attention = "attention";

    public static bool IsDone(string state) => state is Own or Confirmed or Booked;
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
    // Nights a confirmed or booked row holds (NightsCovered also counts asks, for display).
    public int NightsBooked { get; init; }
    // Taxi rows only: plan days and how many a booked or confirmed row covers.
    public int DaysTotal { get; init; }
    public int DaysCovered { get; init; }
    // A confirmed booking whose advance is still asked or waiting for the host.
    public bool AdvanceOpen { get; init; }
    public bool Partial => NightsTotal > 0 && NightsCovered < NightsTotal;
    // Done needs every night and every day held by a confirmed or booked row.
    public bool ShortOfCover => (NightsTotal > 0 && NightsBooked < NightsTotal) || (DaysTotal > 0 && DaysCovered < DaysTotal);
    // The rows and journey behind the state (summary detail); empty for a mark or a todo row.
    public IReadOnlyList<TripEnquiry> Enquiries { get; init; } = Array.Empty<TripEnquiry>();
    public SavedJourney? Journey { get; init; }
    // Taxi row only: the request that drives the row (null = legacy rows or a mark).
    public TripTaxiRequestSummary? TaxiRequest { get; init; }
}

// A booking of the new taxi request tables as the trip reads it (names are the booking's snapshots).
public sealed record TripTaxiBookingSummary(
    Guid BookingId, string Status, DateOnly DateFrom, DateOnly DateTo, string DriverName, string VehicleName, short Seats, string PickupCityName)
{
    public bool IsActive => BookingStates.Active.Contains(Status);
}

// The trip's taxi request (contract section 4): the active one, else the latest; null only when the trip never had one.
public sealed record TripTaxiRequestSummary(Guid RequestId, string State, string View, long Version, TripTaxiBookingSummary? Booking = null)
{
    public bool IsActive => RequestStates.IsActive(State);
    // An active request or booking keeps the trip open (plan lock, completion).
    public bool KeepsTripOpen => IsActive || Booking?.IsActive == true;
}

// Trip detail planLock and taxiRequest (contract section 4), both from the same summary.
public static class TripTaxiRequestFields
{
    // The taxi lock wins; else the booked stay with the earliest check-in that has not ended yet.
    public static TripPlanLockDto PlanLock(TripTaxiRequestSummary? request, IEnumerable<TripEnquiry>? enquiries = null, DateOnly? todayIst = null)
    {
        if (request is { IsActive: true } r && PlanLockReasons.For(r.State) is { } reason)
            return new TripPlanLockDto { Locked = true, Reason = reason, RequestId = r.RequestId };
        var today = todayIst ?? TripExecutionRules.TodayIst(DateTime.UtcNow);
        var stay = (enquiries ?? []).Where(e => StayLockHolds(e, today))
            .OrderBy(e => e.CheckIn).FirstOrDefault();
        return stay != null
            ? new TripPlanLockDto { Locked = true, Reason = PlanLockReasons.StayBookingActive, EnquiryId = stay.Id }
            : new TripPlanLockDto();
    }

    // A booked stay locks the plan only while it can still be cancelled: the cancel rule calls it ended once
    // CheckOut <= today IST, so an ended stay never blocks a delete or an edit that no action could unblock.
    // TripPlanLockReader.BookedStaySql repeats this filter in SQL.
    public static bool StayLockHolds(TripEnquiry e, DateOnly todayIst)
        => e.Kind == TripEnquiryKinds.Stay && TripEnquiryStatuses.IsBooked(e.Status) && (e.CheckOut is not { } checkOut || checkOut > todayIst);

    public static TripTaxiRequestRefDto? Ref(TripTaxiRequestSummary? request)
        => request is { } r ? new TripTaxiRequestRefDto { RequestId = r.RequestId, State = r.State, View = r.View, Version = r.Version } : null;
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
    public int? NightsBooked { get; set; }
    public int? DaysTotal { get; set; }
    public int? DaysCovered { get; set; }
    public bool AdvanceOpen { get; set; }
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
    // Where the traveller left the guided flow (plan.guide.step), only a step the plan really has; null = never started.
    public string? GuideStep { get; set; }
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
    // taxiRequest = the trip's taxi request summary; null keeps the legacy taxi row.
    // The rows the trip parts count: stay:k for every city, then taxi (the question flow rows are gone).
    public static List<TripEasyRow> Build(Trip trip, TripPlan plan, IReadOnlyList<TripEnquiry> enquiries, IReadOnlyList<SavedJourney>? journeys,
        IReadOnlySet<Guid>? revisedPending = null, TripTaxiRequestSummary? taxiRequest = null)
        => BuildAll(trip, plan, enquiries, journeys, revisedPending, taxiRequest).Where(r => r.Key.Kind is TripRowKinds.Stay or TripRowKinds.Taxi).ToList();

    // Every row incl. travel rows; only the save guards use it (old builds still send travel marks).
    public static List<TripEasyRow> BuildAll(Trip trip, TripPlan plan, IReadOnlyList<TripEnquiry> enquiries, IReadOnlyList<SavedJourney>? journeys,
        IReadOnlySet<Guid>? revisedPending = null, TripTaxiRequestSummary? taxiRequest = null)
    {
        var ctx = new Context(plan, trip.StartDate ?? plan.Settings?.StartDate, enquiries, journeys ?? Array.Empty<SavedJourney>(), revisedPending, taxiRequest);
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

    public static bool IsRowDone(TripEasyRow r) => TripRowStates.IsDone(r.State) && !r.Partial && !r.ShortOfCover && !r.AdvanceOpen;

    // Every step key a plan has: gettingThere, comingBack, taxi, stay:k for each city, between:k from city 2.
    public static HashSet<string> StepKeysOf(TripPlan plan)
    {
        var legCount = Math.Max(1, plan.Legs?.Count ?? 1);
        var keys = new HashSet<string> { TripRowKinds.GettingThere, TripRowKinds.ComingBack, TripRowKinds.Taxi };
        for (var k = 1; k <= legCount; k++)
        {
            keys.Add($"stay:{k}");
            if (k >= 2) keys.Add($"between:{k}");
        }
        return keys;
    }

    // The saved guide step when it is a valid key of this plan, else null.
    public static string? GuideStepOf(TripPlan plan)
        => plan.Guide?.Step is { } step && TripStepKeys.IsValid(step) && StepKeysOf(plan).Contains(step) ? step : null;

    // Total 3 parts (Itinerary, Taxi, Stay); Done = parts arranged or skipped (TripParts).
    public static TripProgress Progress(int spotCount, IReadOnlyList<TripEasyRow> rows, DateTime? lastEventAt = null, string? guideStep = null)
    {
        rows = rows.Where(r => r.Key.Kind is TripRowKinds.Stay or TripRowKinds.Taxi).ToList();
        var parts = TripParts.Of(spotCount, rows);
        var next = rows.FirstOrDefault(r => r.State == TripRowStates.Attention || r.NeedsYou)
                   ?? (parts.Stay == TripPartStates.Open ? rows.FirstOrDefault(r => r.Key.Kind == TripRowKinds.Stay && r.State == TripRowStates.Todo) : null)
                   ?? (parts.Taxi == TripPartStates.Open ? rows.FirstOrDefault(r => r.Key.Kind == TripRowKinds.Taxi) : null);
        var counted = rows.Where(r => r.State != TripRowStates.Skipped).ToList();
        return new TripProgress
        {
            Total = TripParts.Count,
            Done = parts.Handled,
            InProgress = counted.Count(r => TripRowStates.IsInProgress(r.State)),
            Attention = counted.Count(r => r.State == TripRowStates.Attention),
            Percent = parts.Percent,
            NeedsYou = rows.Any(r => r.NeedsYou),
            Next = next == null ? null : new TripNextRow { Kind = next.Key.Kind, LegIndex = next.Key.LegIndex, Key = next.Key.Id },
            LastEventAt = lastEventAt,
            GuideStep = guideStep,
            Rows = rows.Select(ToDto).ToList(),
        };
    }

    public static TripProgress Compute(Trip trip, TripPlan plan, IReadOnlyList<TripEnquiry> enquiries, IReadOnlyList<SavedJourney>? journeys,
        IReadOnlySet<Guid>? revisedPending = null, TripTaxiRequestSummary? taxiRequest = null)
        => Progress(plan.Days.Sum(d => d.Spots.Count), Build(trip, plan, enquiries, journeys, revisedPending, taxiRequest),
            enquiries.Count == 0 ? null : enquiries.Max(e => e.UpdatedAt), GuideStepOf(plan));

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
        NightsBooked = row.NightsTotal > 0 ? row.NightsBooked : null,
        DaysTotal = row.DaysTotal > 0 ? row.DaysTotal : null,
        DaysCovered = row.DaysTotal > 0 ? row.DaysCovered : null,
        AdvanceOpen = row.AdvanceOpen,
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
        TripPlan plan, DateOnly? start, IReadOnlyList<TripEnquiry> enquiries, IReadOnlyList<SavedJourney> journeys, IReadOnlySet<Guid>? revisedPending,
        TripTaxiRequestSummary? taxiRequest)
    {
        public TripTaxiRequestSummary? TaxiRequest { get; } = taxiRequest;
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
        IEnumerable<TripEnquiry>? rows = null, SavedJourney? journey = null, int nightsTotal = 0, int nightsCovered = 0,
        int daysTotal = 0, int daysCovered = 0, bool advanceOpen = false, int nightsBooked = 0, TripTaxiRequestSummary? taxiRequest = null)
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
            NightsBooked = nightsBooked,
            DaysTotal = daysTotal,
            DaysCovered = daysCovered,
            AdvanceOpen = advanceOpen,
            TaxiRequest = taxiRequest,
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
        // A night is held only by a confirmed or booked row; an ask never holds one.
        var covered = live.SelectMany(e => TripBookingMap.DaysOf(e, ctx.Start, ctx.DayCount)).Where(days.Contains).Distinct().Count();
        var booked = live.Where(e => TripEnquiryStatuses.IsBooked(e.Status))
            .SelectMany(e => TripBookingMap.DaysOf(e, ctx.Start, ctx.DayCount)).Where(days.Contains).Distinct().Count();
        var advanceOpen = live.Any(e => e.Status == TripEnquiryStatuses.ConfirmedByBoth && AdvanceIsOpen(e));
        TripEasyRow Cover(string state, bool needsYou)
            => Row(TripRowKinds.Stay, k, state, needsYou, asked, live, null, days.Count, covered, advanceOpen: advanceOpen, nightsBooked: booked);

        var both = live.FirstOrDefault(e => e.Status == TripEnquiryStatuses.ConfirmedByBoth);
        if (both == null && moved.Any(e => e.Status == TripEnquiryStatuses.ConfirmedByBoth))
            return Row(TripRowKinds.Stay, k, TripRowStates.Attention, true, 0, moved);
        if (both != null)
        {
            var replace = live.Any(e => e.Id != both.Id && e.Status == TripEnquiryStatuses.Offered);
            var needsYou = both.AdvanceStatus == TripAdvanceStatuses.Pending || ctx.RevisedPending.Contains(both.Id) || replace;
            return Cover(TripRowStates.Confirmed, needsYou);
        }
        if (Has(TripEnquiryStatuses.BookedByTraveller))
            return Cover(TripRowStates.Booked, Has(TripEnquiryStatuses.Offered));
        if (Has(TripEnquiryStatuses.Offered))
            return Cover(TripRowStates.Offer, true);
        if (Has(TripEnquiryStatuses.Asked, TripEnquiryStatuses.Countered))
            return Cover(TripRowStates.Asked, false);
        if (LastClosedIsProblem(ctx, covering)) return Row(TripRowKinds.Stay, k, TripRowStates.Attention, false, 0, covering);
        // A booking outside every leg's days (days cut, start moved) is never a Todo: it needs the traveller.
        var orphans = ctx.Local.Where(e => e.Kind == TripEnquiryKinds.Stay && TripEnquiryStatuses.IsBooked(e.Status)
            && TripBookingMap.DaysOf(e, ctx.Start, ctx.DayCount).Count == 0 && OrphanLeg(e, ctx.Plan) == k).ToList();
        if (orphans.Count > 0) return Row(TripRowKinds.Stay, k, TripRowStates.Attention, true, 0, orphans);
        return Row(TripRowKinds.Stay, k, TripRowStates.Todo);
    }

    private static bool AdvanceIsOpen(TripEnquiry e) => e.AdvanceStatus is TripAdvanceStatuses.Pending or TripAdvanceStatuses.ProofSent;

    private static int OrphanLeg(TripEnquiry e, TripPlan plan) => TripLegRules.HomeLegIndex(e, plan) is { } home && home >= 1 ? home : 1;

    // A live or finished request drives the row; else the mark; else an ended request (todo); legacy local taxi rows only without one.
    private static TripEasyRow TaxiRow(Context ctx)
    {
        var request = ctx.TaxiRequest;
        if (request != null && TaxiRequestViewRules.EasyRowState(request.State) is var state && state != TripRowStates.Todo)
            return Row(TripRowKinds.Taxi, null, state, needsYou: request.State == RequestStates.Collecting, taxiRequest: request);

        var mark = ctx.Plan.TaxiMark;
        if (Marked(mark, TripRowMarkStatuses.Own)) return Row(TripRowKinds.Taxi, null, TripRowStates.Own);
        if (Marked(mark, TripRowMarkStatuses.Skipped)) return Row(TripRowKinds.Taxi, null, TripRowStates.Skipped);
        if (request != null)
            return Row(TripRowKinds.Taxi, null, TripRowStates.Todo, needsYou: request.State == RequestStates.Collecting, taxiRequest: request);

        var covering = Newest(ctx.Local.Where(e => e.Kind == TripEnquiryKinds.Taxi));
        var live = covering.Where(e => TripEnquiryStatuses.Open.Contains(e.Status)).ToList();
        bool Has(params string[] statuses) => live.Any(e => statuses.Contains(e.Status));
        var asked = AskedOf(live);

        // Every plan day needs a confirmed or booked car; an ask covers nothing.
        var required = ctx.Plan.Days.Select(d => d.Day).ToHashSet();
        var covered = live.Where(e => TripEnquiryStatuses.IsBooked(e.Status))
            .SelectMany(e => TripBookingMap.DaysOf(e, ctx.Start, ctx.DayCount)).Where(required.Contains).Distinct().Count();
        var advanceOpen = live.Any(e => e.Status == TripEnquiryStatuses.ConfirmedByBoth && AdvanceIsOpen(e));
        TripEasyRow Cover(string state, bool needsYou)
            => Row(TripRowKinds.Taxi, null, state, needsYou, asked, live, null, 0, 0, required.Count, covered, advanceOpen);

        var both = live.FirstOrDefault(e => e.Status == TripEnquiryStatuses.ConfirmedByBoth);
        if (both != null)
            return Cover(TripRowStates.Confirmed, live.Any(e => e.Id != both.Id && e.Status == TripEnquiryStatuses.FareSent));
        if (Has(TripEnquiryStatuses.BookedByTraveller))
            return Cover(TripRowStates.Booked, Has(TripEnquiryStatuses.FareSent));
        if (Has(TripEnquiryStatuses.FareSent)) return Cover(TripRowStates.Offer, true);
        if (Has(TripEnquiryStatuses.Asked))
            return Cover(TripRowStates.Asked, false);
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
        TripTravelStates.Saved => (TripRowStates.Saved, true),
        TripTravelStates.Free => (TripRowStates.Offer, true),
        TripTravelStates.Booked => (TripRowStates.Booked, false),
        TripTravelStates.Asked or TripTravelStates.PartlyBooked => (TripRowStates.Asked, carNeedsYou),
        TripTravelStates.CarMissing or TripTravelStates.DriverUnavailable or TripTravelStates.DateChanged => (TripRowStates.Attention, false),
        _ => (TripRowStates.Todo, false),
    };
}

public static class TripPartStates
{
    public const string Open = "open", Arranged = "arranged", Skipped = "skipped";
}

// Issue 16 v7: Itinerary, Taxi, Stay; handled = arranged or skipped; percent = handled of 3 (0, 33, 67, 100). The app's TripParts mirrors it.
public sealed record TripParts(string Itinerary, string Taxi, string Stay)
{
    public const int Count = 3;

    public int Handled => new[] { Itinerary, Taxi, Stay }.Count(p => p != TripPartStates.Open);
    public int Percent => (int)Math.Round(Handled * 100.0 / Count);
    public bool Complete => Handled == Count;

    public static bool IsArranged(string state)
        => state is TripRowStates.Asked or TripRowStates.Offer or TripRowStates.Booked or TripRowStates.Confirmed or TripRowStates.Own;

    public static TripParts Of(int spotCount, IReadOnlyList<TripEasyRow> rows)
    {
        var taxiRow = rows.FirstOrDefault(r => r.Key.Kind == TripRowKinds.Taxi);
        var taxi = taxiRow == null ? TripPartStates.Open
            : IsArranged(taxiRow.State) ? TripPartStates.Arranged
            : taxiRow.State == TripRowStates.Skipped ? TripPartStates.Skipped : TripPartStates.Open;
        var stays = rows.Where(r => r.Key.Kind == TripRowKinds.Stay).ToList();
        var stay = stays.Any(r => IsArranged(r.State)) ? TripPartStates.Arranged
            : stays.Count > 0 && stays.All(r => r.State == TripRowStates.Skipped) ? TripPartStates.Skipped : TripPartStates.Open;
        return new TripParts(spotCount > 0 ? TripPartStates.Arranged : TripPartStates.Open, taxi, stay);
    }
}

// Complete my plan (issue 16 v7): the open parts become skipped through the existing marks; nothing else changes.
public static class TripCompletePlan
{
    // true = the plan changed. A stay row in attention that still holds an open enquiry is left alone.
    public static bool Apply(TripPlan plan, IReadOnlyList<TripEasyRow> rows, int spotCount)
    {
        var parts = TripParts.Of(spotCount, rows);
        var changed = false;
        if (parts.Taxi == TripPartStates.Open)
        {
            plan.TaxiMark = new TripRowMark { Status = TripRowMarkStatuses.Skipped };
            changed = true;
        }
        if (parts.Stay != TripPartStates.Open) return changed;
        var legs = plan.Legs ?? new List<TripCityLeg>();
        foreach (var row in rows.Where(r => r.Key.Kind == TripRowKinds.Stay && r.Key.LegIndex is { } k && k >= 1 && k <= legs.Count))
        {
            var open = row.Enquiries.Any(e => TripEnquiryStatuses.Open.Contains(e.Status));
            if (row.State != TripRowStates.Todo && !(row.State == TripRowStates.Attention && !open)) continue;
            var leg = legs[row.Key.LegIndex!.Value - 1];
            if (leg == null) continue;
            leg.StayMark = new TripRowMark { Status = TripRowMarkStatuses.Skipped };
            changed = true;
        }
        return changed;
    }
}

public static class TripCreateRules
{
    // Trips a traveller may keep in Draft at once (TripHandlers.MaxOpenDrafts).
    public const int MaxOpenDrafts = 30;

    public static bool DraftCapReached(int openDrafts) => openDrafts >= MaxOpenDrafts;
}
