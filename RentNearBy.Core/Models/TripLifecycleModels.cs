using System.Text.Json;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;

namespace RentNearBy.Core.Models;

public static class TripProblemKinds
{
    public const string DriverNoShow = "DriverNoShow", HostNoRoom = "HostNoRoom", RoadClosed = "RoadClosed", Other = "Other";
    public static readonly string[] All = [DriverNoShow, HostNoRoom, RoadClosed, Other];

    public static string? FailReasonOf(string kind) => kind switch
    {
        DriverNoShow => TripFailReasons.ProblemDriverNoShow,
        HostNoRoom => TripFailReasons.ProblemHostNoRoom,
        _ => null,
    };

    public static bool MarksBooking(string kind) => kind is DriverNoShow or HostNoRoom;

    public static string ExpectedKind(string problemKind) => problemKind == DriverNoShow ? TripEnquiryKinds.Taxi : TripEnquiryKinds.Stay;
}

public static class TripBookingStates
{
    public const string Draft = "draft", PlanOnly = "planOnly", Confirming = "confirming", Ready = "ready", Completed = "completed";

    // Order: Completed, Draft, PlanOnly, Confirming, Ready, on the Make it easy rows (skipped rows leave the count).
    // PlanOnly = a saved plan with spots and no row done, in progress or needing attention.
    // Ready = every row that is not skipped is done and at least one row is done.
    public static string Compute(Trip trip, TripPlan plan, IReadOnlyList<TripEnquiry> enquiries, IReadOnlyList<SavedJourney>? journeys = null)
    {
        if (trip.Status == TripStatuses.Completed) return Completed;
        var spots = plan.Days.Sum(d => d.Spots.Count);
        if (trip.Status != TripStatuses.Planned || spots == 0) return Draft;
        return OfRows(TripEasyRows.Build(trip, plan, enquiries, journeys));
    }

    public static string OfRows(IReadOnlyList<TripEasyRow> rows)
    {
        var counted = rows.Where(r => r.State != TripRowStates.Skipped).ToList();
        if (counted.All(r => r.State is TripRowStates.Todo or TripRowStates.Saved)) return PlanOnly;
        if (counted.Any(r => r.State is TripRowStates.Asked or TripRowStates.Offer or TripRowStates.Attention)) return Confirming;
        return counted.All(TripEasyRows.IsRowDone) ? Ready : Confirming;
    }


    // An advance counts as open only while the booking is still live: cancel, problem and replace never reset AdvanceStatus.
    public static bool IsOpenForCompletion(TripEnquiry e) =>
        e.Status is TripEnquiryStatuses.Asked or TripEnquiryStatuses.Offered or TripEnquiryStatuses.Countered or TripEnquiryStatuses.FareSent
        || (e.Status == TripEnquiryStatuses.ConfirmedByBoth && e.AdvanceStatus is TripAdvanceStatuses.Pending or TripAdvanceStatuses.ProofSent);

    // Confirmed by the host or driver too; booked by the traveller alone is still waiting.
    public static bool IsSettled(TripEnquiry e) => e.Status == TripEnquiryStatuses.ConfirmedByBoth;
}

// The first ask or booking makes a Draft trip Planned (TripEnquiryHandlers.PromoteToPlannedAsync); a save never puts Draft back.
public static class TripStatusRules
{
    // The status a PUT /trips/{id} writes: null keeps the stored one; Draft over a stored Planned keeps Planned
    // while the plan still has spots (a Planned trip needs spots).
    // A live enquiry keeps a Planned trip Planned (Draft has no reminders and no auto-complete).
    public static string? OnSave(string stored, string? requested, int spots, bool hasLiveEnquiry = false)
        => requested == TripStatuses.Draft && stored == TripStatuses.Planned && (spots > 0 || hasLiveEnquiry) ? TripStatuses.Planned : requested;

    public const string ZeroSpotsLiveMessage = "This trip has live enquiries. Keep at least one place, or withdraw them first.";

    // Removing every place under a live enquiry is refused.
    public static bool RejectsZeroSpots(string stored, int spots, bool hasLiveEnquiry)
        => stored == TripStatuses.Planned && spots == 0 && hasLiveEnquiry;
}

public enum TripCompleteVerdict { Ok, NotPlanned, NotEnded, DatesRequired }

// Complete: Planned only; from the last day, or earlier only when every step is settled and at least one is done.
public static class TripCompleteRules
{
    public const string ClosedReason = "TripCompleted";

    public static TripCompleteVerdict Check(Trip trip, IReadOnlyList<TripEasyRow> rows, DateOnly todayIst)
    {
        if (trip.Status != TripStatuses.Planned) return TripCompleteVerdict.NotPlanned;
        var settled = rows.Any(TripEasyRows.IsRowDone) && TripReminderRules.AllSettled(rows);
        if (trip.StartDate is not { } start) return settled ? TripCompleteVerdict.Ok : TripCompleteVerdict.DatesRequired;
        if (todayIst >= (trip.EndDate ?? start)) return TripCompleteVerdict.Ok;
        return settled ? TripCompleteVerdict.Ok : TripCompleteVerdict.NotEnded;
    }

    // A trip nobody touched (every row still todo or saved) is left for the traveller; the timer never completes it.
    public static bool IsUntouched(IReadOnlyList<TripEasyRow> rows)
        => rows.All(r => r.State is TripRowStates.Todo or TripRowStates.Saved);
}

// plan.guide the server accepts: steps of the plan only, none beyond the first unresolved row, no choice without evidence.
public static class TripGuideGate
{
    public const string StepAhead = "GuideStepAhead";
    public const string UnknownStep = "GuideUnknownStep";

    public sealed record Result(TripGuide? Guide, string? Error);

    private static bool Resolved(string state) => state is TripRowStates.Asked or TripRowStates.Offer or TripRowStates.Confirmed
        or TripRowStates.Booked or TripRowStates.Own or TripRowStates.Skipped;

    // rows = the Make it easy rows of the incoming plan, in guide order.
    public static Result Apply(TripGuide? incoming, TripGuide? stored, IReadOnlyList<TripEasyRow> rows)
    {
        if (incoming == null) return new Result(null, null);
        var order = rows.Select(r => r.Key.Id).ToList();

        if (incoming.Step != null)
        {
            var at = order.IndexOf(incoming.Step);
            if (at < 0) return new Result(null, UnknownStep);
            var firstOpen = rows.ToList().FindIndex(r => !Resolved(r.State));
            if (firstOpen >= 0 && at > firstOpen && incoming.Step != stored?.Step) return new Result(null, StepAhead);
        }

        var states = rows.ToDictionary(r => r.Key.Id, r => r.State);
        var choices = incoming.Choices?
            .Where(c => states.TryGetValue(c.Key, out var state) && Evidenced(c.Value, state))
            .ToDictionary(c => c.Key, c => c.Value);
        return new Result(TripGuideRules.Sanitize(new TripGuide { Step = incoming.Step, Choices = choices, Overview = incoming.Overview }), null);
    }

    private static bool Evidenced(string choice, string state) => choice == TripGuideChoices.Find
        ? state is TripRowStates.Asked or TripRowStates.Offer or TripRowStates.Confirmed or TripRowStates.Booked or TripRowStates.Attention
        : state is TripRowStates.Own or TripRowStates.Skipped;
}

// A new Own or Skip mark over a row the traveller has live work on is refused (a stored mark is never "new").
public static class TripMarkGuard
{
    public const string Code = "MARK_BLOCKED";

    public static string? BlockedKey(IReadOnlyList<TripEasyRow> stored, IReadOnlyList<TripEasyRow> incoming)
    {
        foreach (var row in incoming.Where(r => r.State is TripRowStates.Own or TripRowStates.Skipped))
        {
            var before = stored.FirstOrDefault(r => r.Key.Id == row.Key.Id);
            if (before != null && before.State is TripRowStates.Asked or TripRowStates.Offer or TripRowStates.Confirmed or TripRowStates.Booked)
                return row.Key.Id;
        }
        return null;
    }
}

// A plan edit that cuts a day or moves the start across a confirmed or booked night or taxi day is refused.
public static class TripBookedRange
{
    public const string Code = "BOOKED_IN_RANGE";

    public static bool Strands(DateOnly? oldStart, int oldDays, DateOnly? newStart, int newDays, IEnumerable<TripEnquiry> enquiries)
    {
        if (oldStart is not { } from || newStart is not { } to) return false;
        var last = to.AddDays(newDays - 1);
        foreach (var e in enquiries.Where(e => TripEnquiryStatuses.IsBooked(e.Status) && !TripSegmentRows.IsSegment(e)))
            foreach (var d in TripBookingMap.DaysOf(e, from, oldDays))
            {
                var date = from.AddDays(d - 1);
                if (date < to || date > last) return true;
            }
        return false;
    }
}

// Night (stay) and day (taxi) numbers 1-based, as in the plan.
public static class TripBookingMap
{
    // Every day of a trip has a night, so both kinds run to dayCount. A taxi row covers dates, not plan day numbers:
    // CheckIn + (d - first day), so a start-date change never re-maps a booking.
    public static List<int> DaysOf(TripEnquiry e, DateOnly? start, int dayCount)
    {
        var taxi = e.Kind == TripEnquiryKinds.Taxi;
        var days = new List<int>();

        if (taxi && !string.IsNullOrWhiteSpace(e.DaysJson))
        {
            try { days.AddRange(JsonSerializer.Deserialize<int[]>(e.DaysJson) ?? []); }
            catch (JsonException) { }
        }
        if (taxi && days.Count > 0 && start is { } from && e.CheckIn is { } checkIn)
        {
            var first = days.Min();
            days = days.Select(d => checkIn.AddDays(d - first).DayNumber - from.DayNumber + 1).ToList();
        }
        else if (days.Count == 0 && start is { } s && e.CheckIn is { } a)
        {
            var last = e.CheckOut?.AddDays(-1) ?? a;
            for (var d = a; d <= last; d = d.AddDays(1)) days.Add(d.DayNumber - s.DayNumber + 1);
        }
        if (days.Count == 0 && e.Day is { } one) days.Add(one);
        return days.Where(d => d >= 1 && d <= dayCount).Distinct().ToList();
    }
}

public static class TripTravelStates
{
    public const string NotPlanned = "notPlanned", Own = "own", Saved = "saved", Asked = "asked", Free = "free",
        PartlyBooked = "partlyBooked", Booked = "booked", Confirmed = "confirmed", DateChanged = "dateChanged",
        CarMissing = "carMissing", DriverUnavailable = "driverUnavailable";

    public static readonly string[] All =
        [NotPlanned, Own, Saved, Asked, Free, PartlyBooked, Booked, Confirmed, DateChanged, CarMissing, DriverUnavailable];
}

// The travel tile state of one city leg (CONTRACT_MCB D7, first match wins); the app's gap row is session-only.
public static class TripTravelState
{
    public static string Of(TripTravel? arrival, SavedJourney? journey, IReadOnlyList<TripEnquiry> journeyRows, DateOnly? travelDate)
    {
        if (arrival == null) return TripTravelStates.NotPlanned;
        return OfJourney(TripTravelRules.JourneyIdOf(arrival), journey, journeyRows, travelDate, arrival.OwnCars);
    }

    // Getting there / Coming back: own (or the legacy skipped) is own; a chosen or booked step reads its journey.
    public static string OfStep(TripStep? step, SavedJourney? journey, IReadOnlyList<TripEnquiry> journeyRows, DateOnly? travelDate)
    {
        var status = TripStepRules.StatusOf(step);
        if (status is TripStepStatuses.Own or TripStepStatuses.Skipped) return TripTravelStates.Own;
        if (!TripStepRules.NeedsLink(step)) return TripTravelStates.NotPlanned;
        return OfJourney(step!.JourneyId, journey, journeyRows, travelDate, step.OwnCars);
    }

    // A car in ownCars is arranged by the traveller: it counts as booked and confirmed; every car own reads own.
    private static string OfJourney(
        Guid? linkedId, SavedJourney? journey, IReadOnlyList<TripEnquiry> journeyRows, DateOnly? travelDate, IReadOnlyCollection<int>? ownCars)
    {
        if (journey == null || journey.IsDeleted || linkedId != journey.Id) return TripTravelStates.NotPlanned;
        if (travelDate is { } date && journey.TravelDate != date) return TripTravelStates.DateChanged;

        var allCars = journey.Legs.OrderBy(l => l.LegIndex).ToList();
        if (allCars.Count == 0) return TripTravelStates.Saved;
        var own = ownCars ?? Array.Empty<int>();
        var cars = allCars.Where(c => !own.Contains(c.LegIndex)).ToList();
        if (cars.Count == 0) return TripTravelStates.Own;
        var rows = journeyRows
            .Where(r => r.Status != TripEnquiryStatuses.Closed)
            .Select(r => (Row: r, Car: TripSegmentRows.CarOf(r)))
            .ToList();

        var booked = new List<bool>();
        var confirmed = new List<bool>();
        var missing = false;
        var paused = false;
        foreach (var car in cars)
        {
            var mine = rows.Where(x => x.Car == car.LegIndex).Select(x => x.Row).ToList();
            var isBooked = mine.Any(r => r.Status is TripEnquiryStatuses.BookedByTraveller or TripEnquiryStatuses.ConfirmedByBoth);
            booked.Add(isBooked);
            confirmed.Add(mine.Any(r => r.Status == TripEnquiryStatuses.ConfirmedByBoth));
            if (isBooked) continue;
            var asked = car.LastEnquiryId != null;
            var theirs = mine.Where(r => !ByTraveller(r) && r.ListingId == car.DriverTaxiId).ToList();
            if (car.LastKnownStatus == JourneyLegStatuses.NotFree || (asked && theirs.Count > 0 && theirs.All(IsRed))) missing = true;
            if (!car.DriverAvailable) paused = true;
        }

        if (missing) return TripTravelStates.CarMissing;
        if (paused) return TripTravelStates.DriverUnavailable;
        if (booked.All(b => b)) return confirmed.All(c => c) ? TripTravelStates.Confirmed : TripTravelStates.Booked;
        if (booked.Any(b => b)) return TripTravelStates.PartlyBooked;
        var askedCars = cars.Where(c => c.LastEnquiryId != null).ToList();
        if (askedCars.Count == 0) return TripTravelStates.Saved;
        return askedCars.All(c => c.LastKnownStatus == JourneyLegStatuses.Free) ? TripTravelStates.Free : TripTravelStates.Asked;
    }

    // The journey was deleted or unlinked while its car rows remain: booked cars still drive the state.
    // cars = the arrival's car count (0 when unknown); a booked row on another date than the travel day reads dateChanged.
    public static string OfRows(IReadOnlyList<TripEnquiry> journeyRows, int cars, DateOnly? travelDate)
    {
        var rows = journeyRows
            .Where(r => r.Status != TripEnquiryStatuses.Closed)
            .Select(r => (Row: r, Car: TripSegmentRows.CarOf(r) ?? 0))
            .Where(x => x.Car > 0)
            .ToList();
        static bool IsBookedRow(TripEnquiry r) => r.Status is TripEnquiryStatuses.BookedByTraveller or TripEnquiryStatuses.ConfirmedByBoth;

        var bookedRows = rows.Where(x => IsBookedRow(x.Row)).ToList();
        if (bookedRows.Count == 0) return TripTravelStates.NotPlanned;
        if (travelDate is { } date && bookedRows.Any(x => x.Row.CheckIn is { } d && d != date)) return TripTravelStates.DateChanged;

        var total = Math.Max(cars, rows.Max(x => x.Car));
        var booked = bookedRows.Select(x => x.Car).Distinct().Count();
        if (booked < total) return TripTravelStates.PartlyBooked;
        var confirmed = bookedRows.Where(x => x.Row.Status == TripEnquiryStatuses.ConfirmedByBoth).Select(x => x.Car).Distinct().Count();
        return confirmed == total ? TripTravelStates.Confirmed : TripTravelStates.Booked;
    }

    private static bool IsRed(TripEnquiry e) => e.Status is TripEnquiryStatuses.NotAvailable or TripEnquiryStatuses.Declined
        or TripEnquiryStatuses.Expired or TripEnquiryStatuses.Failed
        or TripEnquiryStatuses.CancelledByOwner or TripEnquiryStatuses.CancelledByTraveller;

    // The traveller's own cancel or decline is neutral: it never makes a car read as missing.
    private static bool ByTraveller(TripEnquiry e) => e.Status is TripEnquiryStatuses.CancelledByTraveller or TripEnquiryStatuses.Declined;
}

public static class TripReminderRules
{
    public const int NoReplyHours = 24, WindowHours = 48;
    public const int DaysBeforeStart = 3, StartsInHourIst = 9, TodayHourIst = 7, MarkCompleteHourIst = 10, AutoCompleteDays = 7;
    public const int TravelEveHourIst = 18;

    // The one clock: every shortlist and enquiry reminder goes out once a morning at this IST time (08:00 IST, owner decision 2026-11-12).
    public const int MorningHourIst = 8, MorningMinuteIst = 0, CatchUpEndHourIst = 12;

    // A host is reminded only when the enquiry is at least this old at run time (the traveller side waits TravellerMinAgeHours).
    public const int HostMinAgeHours = 12;
    // The traveller's first reminder is the next morning run that is at least this many hours after the action (add or enquiry).
    public const int TravellerMinAgeHours = 3;
    // The final push is only for rows that ended in the last days; older unannounced rows are stamped by the daily purge.
    public const int FinalWindowDays = 2;
    public const int SentShortlistRetentionDays = 30;

    // No finished run for today and the catch-up window (until 12:00 IST) is over: ops must be alerted.
    public static bool RunMissed(DateTime nowUtc, bool todayFinished)
        => !todayFinished && nowUtc >= IstTime.AtUtc(IstTime.TodayIst(nowUtc), CatchUpEndHourIst);

    public static DateTime MorningDueUtc(DateOnly dateIst) => MorningDueUtc(dateIst, MorningHourIst, MorningMinuteIst);
    public static DateTime MorningDueUtc(DateOnly dateIst, int hour, int minute) => IstTime.AtUtc(dateIst, hour, minute);

    public static DateTime FirstTravellerMorningUtc(DateTime actionUtc)
    {
        var due = MorningDueUtc(IstTime.TodayIst(actionUtc));
        while (actionUtc >= due.AddHours(-TravellerMinAgeHours)) due = due.AddDays(1);
        return due;
    }

    public static DateTime NextMorningUtc(DateTime nowUtc) => NextMorningUtc(nowUtc, MorningHourIst, MorningMinuteIst);
    public static DateTime NextMorningUtc(DateTime nowUtc, int hour, int minute)
    {
        var today = IstTime.TodayIst(nowUtc);
        var due = MorningDueUtc(today, hour, minute);
        return due > nowUtc ? due : MorningDueUtc(today.AddDays(1), hour, minute);
    }

    // Run (or catch up after a restart) only from the morning time until 12:00 IST, and only once per IST date: never in the afternoon.
    public static bool ShouldRunMorning(DateTime nowUtc, bool todayFinished) => ShouldRunMorning(nowUtc, todayFinished, MorningHourIst, MorningMinuteIst);
    public static bool ShouldRunMorning(DateTime nowUtc, bool todayFinished, int hour, int minute)
    {
        var today = IstTime.TodayIst(nowUtc);
        return !todayFinished && nowUtc >= MorningDueUtc(today, hour, minute) && nowUtc < IstTime.AtUtc(today, CatchUpEndHourIst);
    }

    public static string TravellerKey(Guid userId, DateOnly dayIst) => $"rem:t:{userId:N}:{dayIst:yyyyMMdd}";
    public static string HostKey(Guid userId, DateOnly dayIst) => $"rem:h:{userId:N}:{dayIst:yyyyMMdd}";

    public static DateTime StartsInDueUtc(DateOnly start) => TripExecutionRules.IstMidnightUtc(start.AddDays(-DaysBeforeStart)).AddHours(StartsInHourIst);
    public static DateTime TravelEveDueUtc(DateOnly travelDate) => TripExecutionRules.IstMidnightUtc(travelDate.AddDays(-1)).AddHours(TravelEveHourIst);

    // The eve only (18:00 to 24:00 IST): a "Tomorrow" push never goes out on the travel day itself.
    public static bool TravelEveInWindow(DateOnly travelDate, DateTime nowUtc) => IstTime.InEveWindow(travelDate, nowUtc, TravelEveHourIst);
    public static DateTime TodayDueUtc(DateOnly start) => TripExecutionRules.IstMidnightUtc(start).AddHours(TodayHourIst);
    public static DateTime MarkCompleteDueUtc(DateOnly end) => TripExecutionRules.IstMidnightUtc(end.AddDays(1)).AddHours(MarkCompleteHourIst);

    // A reminder is attempted only inside [due, due + 48 h).
    public static bool InWindow(DateTime dueUtc, DateTime nowUtc) => nowUtc >= dueUtc && nowUtc < dueUtc.AddHours(WindowHours);

    // d3 keeps the 48 h catch-up (its body recomputes the days left); d0 and done speak of one IST day and close with it.
    public static bool StartsInOpen(DateOnly start, DateTime nowUtc) => InWindow(StartsInDueUtc(start), nowUtc);
    public static bool TodayOpen(DateOnly start, DateTime nowUtc)
        => IstTime.InWindow(TodayDueUtc(start), IstTime.IstMidnightUtc(start.AddDays(1)), nowUtc);
    public static bool MarkCompleteOpen(DateOnly end, DateTime nowUtc)
        => IstTime.InWindow(MarkCompleteDueUtc(end), IstTime.IstMidnightUtc(end.AddDays(2)), nowUtc);

    // The dates whose reminder window is open now (a few days around today covers every window above).
    private const int DateScanDays = DaysBeforeStart + 3;
    public static DateOnly[] StartsInOpenDates(DateTime nowUtc) => IstTime.DatesWhere(nowUtc, DateScanDays, DateScanDays, StartsInOpen);
    public static DateOnly[] TodayOpenDates(DateTime nowUtc) => IstTime.DatesWhere(nowUtc, DateScanDays, DateScanDays, TodayOpen);
    public static DateOnly[] MarkCompleteOpenDates(DateTime nowUtc) => IstTime.DatesWhere(nowUtc, DateScanDays, DateScanDays, MarkCompleteOpen);

    public static bool AutoCompleteDue(DateOnly end, DateOnly todayIst) => end.AddDays(AutoCompleteDays) <= todayIst;
    public static bool IsRunning(Trip t, DateOnly todayIst) => t.StartDate is { } s && t.EndDate is { } e && s <= todayIst && todayIst <= e;
    // A trip with no dates can be completed any time; a dated one from its first day (running or after).
    public static bool CanComplete(Trip t, DateOnly todayIst) => t.StartDate is not { } s || todayIst >= s;

    public static int DaysLeft(DateOnly start, DateOnly todayIst) => start.DayNumber - todayIst.DayNumber;

    // The 3-day push: a live Planned trip with something left to settle. A plan with no choice at all is nagged only
    // when its city has supply to act on (DistrictId set).
    public static bool ShouldRemindStartsIn(Trip trip, TripPlan plan, IReadOnlyList<TripEnquiry> enquiries, DateOnly todayIst,
        IReadOnlyList<SavedJourney>? journeys = null)
    {
        if (trip.StartDate is not { } start || DaysLeft(start, todayIst) <= 0) return false;
        var rows = TripEasyRows.Build(trip, plan, enquiries, journeys);
        return TripBookingStates.Compute(trip, plan, enquiries, journeys) switch
        {
            TripBookingStates.PlanOnly => trip.DistrictId != null,
            TripBookingStates.Confirming => !AllSettled(rows),
            _ => false,
        };
    }

    // The 3-day push also goes out when Getting there is the only thing left to sort.
    public static bool ShouldSendStartsIn(Trip trip, TripPlan plan, IReadOnlyList<TripEnquiry> enquiries, DateOnly todayIst,
        IReadOnlyList<SavedJourney>? journeys = null)
        => trip.StartDate is { } start && DaysLeft(start, todayIst) > 0
           && (ShouldRemindStartsIn(trip, plan, enquiries, todayIst, journeys) || NeedsGettingThereNudge(plan, TripEasyRows.Build(trip, plan, enquiries, journeys)));

    public static string StartsInBody(Trip trip, TripPlan plan, IReadOnlyList<TripEnquiry> enquiries, DateOnly todayIst,
        IReadOnlyList<SavedJourney>? journeys = null)
        => ShouldRemindStartsIn(trip, plan, enquiries, todayIst, journeys) ? StartsInSummary(trip, plan, enquiries, journeys) : GettingThereNudge;

    public static bool ProblemAllowed(Trip t, DateOnly todayIst) => t.StartDate is { } s && t.EndDate is { } e && s <= todayIst && todayIst <= e.AddDays(1);

    public static string StartsInKey(Guid tripId, DateOnly start) => $"d3:{tripId}:{start:yyyyMMdd}";
    public static string TodayKey(Guid tripId, DateOnly start) => $"d0:{tripId}:{start:yyyyMMdd}";
    public static string MarkCompleteKey(Guid tripId, DateOnly end) => $"done:{tripId}:{end:yyyyMMdd}";
    public static string TravelEveKey(Guid tripId, DateOnly travelDate) => $"trv:{tripId}:{travelDate:yyyyMMdd}";

    // "Haridwar" | "Haridwar → Badrinath", cut to max with "…".
    public static string RouteTitle(IReadOnlyList<string> cities, int max = 40)
    {
        if (cities.Count == 1) return cities[0];
        var text = string.Join(" → ", cities);
        return text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";
    }

    public const string GettingThereNudge = "Getting there not sorted.";

    // Only when we know he starts elsewhere and Getting there still has nothing; every other state silences it.
    public static bool NeedsGettingThereNudge(TripPlan plan, IReadOnlyList<TripEasyRow> rows)
        => plan.Steps?.StartFrom != null
           && rows.FirstOrDefault(r => r.Key.Kind == TripRowKinds.GettingThere)?.State == TripRowStates.Todo;

    // "Stay confirmed. No taxi yet." | "1 stay enquiry waiting. Taxi confirmed." (+ " Travel booked.") (+ " Getting there not sorted.")
    // A skipped row says nothing.
    public static string StartsInSummary(Trip trip, TripPlan plan, IReadOnlyList<TripEnquiry> enquiries, IReadOnlyList<SavedJourney>? journeys = null)
    {
        var rows = TripEasyRows.Build(trip, plan, enquiries, journeys);
        var parts = new List<string>();
        if (KindSentence(rows, TripRowKinds.Stay, "stay", "No stay yet.", "Stay confirmed.") is { } stay) parts.Add(stay);
        if (KindSentence(rows, TripRowKinds.Taxi, "taxi", "No taxi yet.", "Taxi confirmed.") is { } taxi) parts.Add(taxi);
        if (TravelSentence(rows) is { } travel) parts.Add(travel);
        if (NeedsGettingThereNudge(plan, rows)) parts.Add(GettingThereNudge);
        return string.Join(' ', parts);
    }

    private static string? KindSentence(IReadOnlyList<TripEasyRow> rows, string kind, string noun, string none, string done)
    {
        var mine = rows.Where(r => r.Key.Kind == kind && r.State != TripRowStates.Skipped).ToList();
        if (mine.Count == 0) return null;
        var open = mine.Where(r => TripRowStates.IsInProgress(r.State)).Sum(r => Math.Max(1, r.AskedCount));
        if (open > 0) return $"{open} {noun} {(open == 1 ? "enquiry" : "enquiries")} waiting.";
        return mine.All(TripEasyRows.IsRowDone) ? done : none;
    }

    // "Travel booked." | "1 travel not booked." | null (one city, or no travel chosen yet).
    private static string? TravelSentence(IReadOnlyList<TripEasyRow> rows)
    {
        var planned = rows.Where(r => r.Key.Kind == TripRowKinds.Between && r.State is not (TripRowStates.Todo or TripRowStates.Skipped)).ToList();
        if (planned.Count == 0) return null;
        var open = planned.Count(r => !TripRowStates.IsDone(r.State));
        return open == 0 ? "Travel booked." : $"{open} travel not booked.";
    }

    // "Pickup 8:30 am. Tap for the driver's number." when a booked local taxi of day 1 has a pickup time, else
    // "Tap for your bookings." Journey cars are never day 1; their pickup goes out in the travel-eve push.
    public static string TodayBody(IReadOnlyList<TripEnquiry> enquiries, DateOnly start)
    {
        var time = enquiries
            .Where(e => e.Kind == TripEnquiryKinds.Taxi && TripEnquiryStatuses.IsBooked(e.Status)
                && !TripSegmentRows.IsSegment(e) && e.CheckIn == start)
            .OrderBy(e => e.CheckIn)
            .Select(e => TripEnquiryText.TimeText(e.PickupTime))
            .FirstOrDefault(t => t != null);
        return time != null ? $"Pickup {time}. Tap for the driver's number." : "Tap for your bookings.";
    }

    // Every row that is not skipped is done.
    public static bool AllSettled(IReadOnlyList<TripEasyRow> rows)
        => rows.Where(r => r.State != TripRowStates.Skipped).All(TripEasyRows.IsRowDone);
}

// The 18:00 IST push the evening before a travel day between two cities (legIndex >= 2).
public static class TripTravelEveReminder
{
    public const int TitleMax = 60;

    public static (string Title, string Body) Build(
        TripPlan plan, int legIndex, DateOnly travelDate, IReadOnlyList<TripEnquiry> enquiries,
        SavedJourney? journey, IReadOnlyDictionary<Guid, JourneyTaxiInfo> taxis)
    {
        var legs = plan.Legs ?? new List<TripCityLeg>();
        var leg = legIndex >= 1 && legIndex <= legs.Count ? legs[legIndex - 1] : null;
        var previous = legIndex >= 2 && legIndex - 1 <= legs.Count ? legs[legIndex - 2] : null;
        var arrival = leg?.Arrival;

        var from = TripTextSafety.Clean(previous?.CityName);
        if (from.Length == 0) from = TripTextSafety.Clean(arrival?.FromName);
        var to = TripTextSafety.Clean(leg?.CityName);
        var title = $"Tomorrow: {from} → {to}";
        if (title.Length > TitleMax) title = title[..(TitleMax - 1)].TrimEnd() + "…";

        return (title, Body(arrival, leg?.TravelMark?.Status == TripRowMarkStatuses.Own, travelDate, enquiries, journey, taxis));
    }

    private static string Body(
        TripTravel? arrival, bool own, DateOnly travelDate, IReadOnlyList<TripEnquiry> enquiries,
        SavedJourney? journey, IReadOnlyDictionary<Guid, JourneyTaxiInfo> taxis)
    {
        if (own) return "You'll arrange your travel. Open the trip.";
        if (arrival == null) return "No travel planned yet. Open the trip.";

        if (journey == null || journey.IsDeleted || TripTravelRules.JourneyIdOf(arrival) != journey.Id || journey.TravelDate != travelDate)
            return "Travel date changed. Check your drivers.";

        var booked = TripSegmentRows.OfJourney(enquiries, journey.Id)
            .Where(r => r.Status is TripEnquiryStatuses.BookedByTraveller or TripEnquiryStatuses.ConfirmedByBoth)
            .ToList();
        var cars = journey.Legs.OrderBy(l => l.LegIndex).ToList();
        var bookedCars = cars
            .Select(car => (Car: car, Row: booked
                .Where(r => TripSegmentRows.CarOf(r) == car.LegIndex)
                .OrderByDescending(r => r.UpdatedAt)
                .FirstOrDefault()))
            .Where(x => x.Row != null)
            .ToList();
        if (bookedCars.Count == 0) return "No driver booked yet. Open the trip.";

        var (first, row) = bookedCars[0];
        var name = taxis.TryGetValue(first.DriverTaxiId, out var taxi) ? TripTextSafety.Clean(taxi.DriverName) : string.Empty;
        if (name.Length == 0) name = "Your driver";
        var time = TripEnquiryText.TimeText(row!.PickupTime);
        var lead = time == null ? name : $"{name} {time}";

        var total = Math.Max(cars.Count, 1);
        var missing = total - bookedCars.Count;
        return missing <= 0
            ? $"{lead}. Tap for the drivers' numbers."
            : $"{lead}. {missing} {(missing == 1 ? "car" : "cars")} not booked yet.";
    }
}

// A Problem on day D fails only the nights/days >= D of a multi-night booking; earlier ones were used and stay booked.
// Plan mutates e in place: it becomes the kept (shrunk) row, or the failed row when nothing before D is kept.
public static class TripProblemSplit
{
    private static DateOnly DateOf(DateOnly start, int day) => start.AddDays(day - 1);

    // False = the booking has no night/day on or after D, so there is nothing to fail.
    public static bool Affects(TripEnquiry e, DateOnly start, int day)
    {
        if (e.Kind == TripEnquiryKinds.Taxi) return TaxiDays(e, start).Any(x => x.PlanDay >= day);
        return e.CheckOut is not { } checkOut || DateOf(start, day) < checkOut;
    }

    public static (TripEnquiry? Kept, TripEnquiry Failed) Plan(TripEnquiry e, DateOnly start, int day, DateTime nowUtc)
        => e.Kind == TripEnquiryKinds.Taxi ? PlanTaxi(e, start, day, nowUtc) : PlanStay(e, start, day, nowUtc);

    private static (TripEnquiry? Kept, TripEnquiry Failed) PlanStay(TripEnquiry e, DateOnly start, int day, DateTime nowUtc)
    {
        var failReason = TripProblemKinds.FailReasonOf(TripProblemKinds.HostNoRoom);
        var date = DateOf(start, day);
        if (e.CheckIn is not { } checkIn || e.CheckOut is not { } checkOut || date <= checkIn || date >= checkOut)
            return (null, FailWhole(e, failReason, nowUtc));

        var total = checkOut.DayNumber - checkIn.DayNumber;
        var kept = date.DayNumber - checkIn.DayNumber;
        var keptTotal = e.AgreedTotal is { } agreed ? Share(agreed, kept, total) : (int?)null;

        var failed = CloneFor(e, nowUtc);
        failed.Day = (short)day;
        failed.CheckIn = date;
        failed.CheckOut = checkOut;
        failed.AgreedTotal = e.AgreedTotal is { } all && keptTotal is { } k ? all - k : null;
        failed.Status = TripEnquiryStatuses.Failed;
        failed.FailReason = failReason;
        SplitAdvance(e, failed, keptTotal);

        e.CheckOut = date;
        e.AgreedTotal = keptTotal;
        e.UpdatedAt = nowUtc;
        RebuildContext(e, d => d >= checkIn && d < date, (ctx, a, b) => TripEnquiryText.Build(ctx, a, b, b.DayNumber - a.DayNumber > 1));
        RebuildContext(failed, d => d >= date && d < checkOut, (ctx, a, b) => TripEnquiryText.Build(ctx, a, b, b.DayNumber - a.DayNumber > 1));
        return (e, failed);
    }

    // Paid money covers the kept nights first; the rest stays on the failed row as paid (refund due). An asked amount
    // follows the kept total and the failed row asks nothing. Runs before e.AgreedTotal moves.
    private static void SplitAdvance(TripEnquiry e, TripEnquiry failed, int? keptTotal)
    {
        if (e.AdvanceAmount is not { } advance || keptTotal is not { } kept) return;
        if (!TripAdvanceSplit.MoneyMoved(e.AdvanceStatus))
        {
            e.AdvanceAmount = TripAdvanceSplit.Scaled(advance, e.AgreedTotal ?? kept, kept);
            return;
        }

        var (parts, refund) = TripAdvanceSplit.Sequential(advance, [kept]);
        e.AdvanceAmount = parts[0];
        if (refund <= 0) return;
        failed.AdvanceStatus = e.AdvanceStatus;
        failed.AdvanceAmount = refund;
        failed.AdvanceAskedAt = e.AdvanceAskedAt;
        failed.ProofSentAt = e.ProofSentAt;
        failed.AdvanceReceivedAt = e.AdvanceReceivedAt;
    }

    // The row's context keeps only the days it still covers, and its text follows its own dates.
    private static void RebuildContext(TripEnquiry row, Func<DateOnly, bool> covers, Func<TripEnquiryContext, DateOnly, DateOnly, string> text)
    {
        if (string.IsNullOrWhiteSpace(row.ContextJson) || row.CheckIn is not { } checkIn || row.CheckOut is not { } checkOut) return;
        try
        {
            var ctx = JsonSerializer.Deserialize<TripEnquiryContext>(row.ContextJson, TripJson.Options);
            if (ctx == null) return;
            ctx.Days = ctx.Days.Where(d => d.Date is not { } x || covers(x)).ToList();
            ctx.Text = text(ctx, checkIn, checkOut);
            row.ContextJson = JsonSerializer.Serialize(ctx, TripJson.Options);
        }
        catch (JsonException) { }
    }

    // The accepted terms as they stand after the split: same rooms and terms, the kept nights and total.
    public static TripOffer ShrinkAcceptedOffer(TripOffer accepted, TripEnquiry kept, DateTime nowUtc) => new()
    {
        Id = Guid.NewGuid(),
        TripEnquiryId = kept.Id,
        Kind = accepted.Kind,
        SenderUserId = accepted.SenderUserId,
        CheckIn = kept.CheckIn,
        CheckOut = kept.CheckOut,
        LinesJson = accepted.LinesJson,
        Nights = (short)TripExecutionRules.Nights(kept.CheckIn, kept.CheckOut),
        Total = kept.AgreedTotal ?? accepted.Total,
        Inclusions = accepted.Inclusions,
        AdvanceAmount = TripAdvanceSplit.ForTerms(
            accepted.AdvanceAmount, accepted.Total, kept.AgreedTotal ?? accepted.Total, kept.AdvanceStatus, kept.AdvanceAmount),
        BalanceNote = accepted.BalanceNote,
        CancellationText = accepted.CancellationText,
        GstInvoice = accepted.GstInvoice,
        Note = accepted.Note,
        Adults = accepted.Adults,
        Kids = accepted.Kids,
        ValidUntil = accepted.ValidUntil,
        Status = TripOfferStatuses.Accepted,
        RespondedAt = nowUtc,
        CreatedAt = nowUtc,
    };

    // Day numbers in DaysJson are the plan's at send time; the row covers CheckIn + (d - first), so the plan day is found by date.
    private static (TripEnquiry? Kept, TripEnquiry Failed) PlanTaxi(TripEnquiry e, DateOnly start, int day, DateTime nowUtc)
    {
        var failReason = TripProblemKinds.FailReasonOf(TripProblemKinds.DriverNoShow);
        var days = TaxiDays(e, start);
        var keptDays = days.Where(x => x.PlanDay < day).ToList();
        var failedDays = days.Where(x => x.PlanDay >= day).ToList();
        if (keptDays.Count == 0 || failedDays.Count == 0) return (null, FailWhole(e, failReason, nowUtc));

        var first = days[0].Stored;
        var firstDate = e.CheckIn ?? DateOf(start, days[0].PlanDay);
        var failed = CloneFor(e, nowUtc);
        failed.DaysJson = JsonSerializer.Serialize(failedDays.Select(x => x.Stored));
        failed.Day = (short)failedDays[0].Stored;
        failed.CheckIn = firstDate.AddDays(failedDays[0].Stored - first);
        failed.CheckOut = firstDate.AddDays(failedDays[^1].Stored - first + 1);
        failed.AgreedFare = e.AgreedFare is { } fare ? fare - Share(fare, keptDays.Count, days.Count) : null;
        failed.Status = TripEnquiryStatuses.Failed;
        failed.FailReason = failReason;

        e.DaysJson = JsonSerializer.Serialize(keptDays.Select(x => x.Stored));
        e.CheckOut = firstDate.AddDays(keptDays[^1].Stored - first + 1);
        e.AgreedFare = e.AgreedFare is { } f ? Share(f, keptDays.Count, days.Count) : null;
        e.UpdatedAt = nowUtc;
        var keptDates = keptDays.Select(x => firstDate.AddDays(x.Stored - first)).ToHashSet();
        var failedDates = failedDays.Select(x => firstDate.AddDays(x.Stored - first)).ToHashSet();
        RebuildContext(e, keptDates.Contains, TripEnquiryText.BuildLead);
        RebuildContext(failed, failedDates.Contains, TripEnquiryText.BuildLead);
        return (e, failed);
    }

    private static List<(int Stored, int PlanDay)> TaxiDays(TripEnquiry e, DateOnly start)
    {
        var stored = new List<int>();
        if (!string.IsNullOrWhiteSpace(e.DaysJson))
        {
            try { stored.AddRange(JsonSerializer.Deserialize<int[]>(e.DaysJson) ?? []); }
            catch (JsonException) { }
        }
        if (stored.Count == 0 && e.CheckIn is { } a)
        {
            var last = e.CheckOut?.AddDays(-1) ?? a;
            for (var d = a; d <= last; d = d.AddDays(1)) stored.Add(d.DayNumber - a.DayNumber + 1);
        }
        if (stored.Count == 0 && e.Day is { } one) stored.Add(one);
        stored = stored.Where(d => d >= 1).Distinct().OrderBy(d => d).ToList();
        if (stored.Count == 0) return [];

        var first = stored[0];
        return stored
            .Select(d => (d, e.CheckIn is { } checkIn ? checkIn.AddDays(d - first).DayNumber - start.DayNumber + 1 : d))
            .ToList();
    }

    private static int Share(int amount, int part, int whole) => MoneySplit.ProRata(amount, part, whole);

    private static TripEnquiry FailWhole(TripEnquiry e, string? failReason, DateTime nowUtc)
    {
        e.Status = TripEnquiryStatuses.Failed;
        e.FailReason = failReason;
        e.UpdatedAt = nowUtc;
        return e;
    }

    private static TripEnquiry CloneFor(TripEnquiry e, DateTime nowUtc) => new()
    {
        Id = Guid.NewGuid(),
        TripId = e.TripId,
        TravellerUserId = e.TravellerUserId,
        OwnerUserId = e.OwnerUserId,
        Kind = e.Kind,
        ListingId = e.ListingId,
        ListingName = e.ListingName,
        ListingLatitude = e.ListingLatitude,
        ListingLongitude = e.ListingLongitude,
        ConversationId = e.ConversationId,
        ContextJson = e.ContextJson,
        ExpiresAt = e.ExpiresAt,
        AdvanceStatus = TripAdvanceStatuses.None,
        ListedRateSnapshot = e.ListedRateSnapshot,
        DriverFare = e.DriverFare,
        TollExtra = e.TollExtra,
        PickupTime = e.PickupTime,
        PickupPlace = e.PickupPlace,
        BookedByTravellerAt = e.BookedByTravellerAt,
        CreatedAt = nowUtc,
        UpdatedAt = nowUtc,
    };
}
