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

    // Order: Completed, Draft, PlanOnly, Confirming, Ready. A red row that no settled row of the same kind replaces counts as Confirming.
    // PlanOnly = a saved plan with spots and either no start date or no stay/taxi choice and no live enquiry on any day.
    public static string Compute(Trip trip, TripPlan plan, IReadOnlyList<TripEnquiry> enquiries)
    {
        if (trip.Status == TripStatuses.Completed) return Completed;
        var start = trip.StartDate ?? plan.Settings?.StartDate;
        var spots = plan.Days.Sum(d => d.Spots.Count);
        if (trip.Status != TripStatuses.Planned || spots == 0) return Draft;

        var map = TripBookingMap.Build(plan, enquiries);
        if (start == null || map.IsEmpty) return PlanOnly;
        if (map.HasOpen || map.HasUnreplacedRed) return Confirming;
        if (!map.StayAny.IsSubsetOf(map.StaySettled) || !map.TaxiAny.IsSubsetOf(map.TaxiSettled)
            || !map.SegmentAny.IsSubsetOf(map.SegmentSettled)) return Confirming;
        return map.StaySettled.Count + map.TaxiSettled.Count + map.SegmentSettled.Count(s => s.Car > 0) == 0 ? Confirming : Ready;
    }

    // An advance counts as open only while the booking is still live: cancel, problem and replace never reset AdvanceStatus.
    public static bool IsOpenForCompletion(TripEnquiry e) =>
        e.Status is TripEnquiryStatuses.Asked or TripEnquiryStatuses.Offered or TripEnquiryStatuses.Countered or TripEnquiryStatuses.FareSent
        || (e.Status == TripEnquiryStatuses.ConfirmedByBoth && e.AdvanceStatus is TripAdvanceStatuses.Pending or TripAdvanceStatuses.ProofSent);

    // Green or blue.
    public static bool IsSettled(TripEnquiry e) => e.Status is TripEnquiryStatuses.ConfirmedByBoth or TripEnquiryStatuses.BookedByTraveller;
}

// The first ask or booking makes a Draft trip Planned (TripEnquiryHandlers.PromoteToPlannedAsync); a save never puts Draft back.
public static class TripStatusRules
{
    // The status a PUT /trips/{id} writes: null keeps the stored one; Draft over a stored Planned keeps Planned
    // while the plan still has spots (a Planned trip needs spots).
    public static string? OnSave(string stored, string? requested, int spots)
        => requested == TripStatuses.Draft && stored == TripStatuses.Planned && spots > 0 ? TripStatuses.Planned : requested;
}

// Confirmed and total exclude own and skipped nights/days; those are counted in StaysOwn / TaxisOwn.
// Travel* = journey cars between cities (own travel excluded); taxi counts never include them, AgreedTotal does.
public sealed record TripBookingCounts(
    int StaysConfirmed, int StaysTotal, int TaxisConfirmed, int TaxisTotal, int AgreedTotal, int StaysOwn, int TaxisOwn,
    int TravelConfirmed = 0, int TravelTotal = 0);

// Night (stay) and day (taxi) numbers 1-based, as in the plan.
internal sealed class TripBookingMap
{
    public readonly HashSet<int> StayAny = new(), StaySettled = new(), TaxiAny = new(), TaxiSettled = new(), TaxiBoth = new();
    // Plan-only nights (own stay, No stay tonight) and own taxi days; a day a live enquiry covers is not in these sets.
    public readonly HashSet<int> StayOwn = new(), StaySkipped = new(), TaxiOwn = new(), TaxiSkipped = new();
    // Travel between cities by (travel day, car); own travel is car 0.
    public readonly HashSet<(int Day, int Car)> SegmentAny = new(), SegmentSettled = new(), SegmentOwn = new();
    public bool HasOpen, HasUnreplacedRed, HasLiveEnquiry;
    public int OpenStays, OpenTaxis;

    // Own travel (car 0) says nothing about nights or days, so it never makes the plan non-empty.
    public bool IsEmpty => StayAny.Count == 0 && TaxiAny.Count == 0 && !SegmentAny.Any(s => s.Car > 0) && !HasLiveEnquiry;

    private static bool IsRed(TripEnquiry e) => e.Status is TripEnquiryStatuses.NotAvailable or TripEnquiryStatuses.Expired
        or TripEnquiryStatuses.Declined or TripEnquiryStatuses.Failed
        or TripEnquiryStatuses.CancelledByOwner or TripEnquiryStatuses.CancelledByTraveller;

    public static TripBookingMap Build(TripPlan plan, IReadOnlyList<TripEnquiry> enquiries)
    {
        var map = new TripBookingMap();
        var count = plan.Days.Count;
        var start = plan.Settings?.StartDate;

        foreach (var day in plan.Days)
        {
            if (day.Day >= 1 && day.Day <= count && day.Stay != null)
            {
                map.StayAny.Add(day.Day);
                if (day.Stay.IsOwn == true)
                {
                    map.StaySettled.Add(day.Day);
                    map.StayOwn.Add(day.Day);
                }
                else if (day.Stay.Skipped == true)
                {
                    map.StaySettled.Add(day.Day);
                    map.StaySkipped.Add(day.Day);
                }
            }
            if (day.Day >= 1 && day.Day <= count && day.Taxi != null)
            {
                map.TaxiAny.Add(day.Day);
                if (day.Taxi.IsOwn == true)
                {
                    map.TaxiSettled.Add(day.Day);
                    map.TaxiOwn.Add(day.Day);
                }
                else if (day.Taxi.Skipped == true)
                {
                    map.TaxiSettled.Add(day.Day);
                    map.TaxiSkipped.Add(day.Day);
                }
                else if (day.Taxi.Booking != null) map.TaxiSettled.Add(day.Day);
            }
        }

        // Current journey per travel day; a segment row of any other journey (replaced or removed) is ignored everywhere.
        var journeys = new Dictionary<Guid, int>();
        var legs = plan.Legs ?? new List<TripCityLeg>();
        for (var i = 1; i < legs.Count; i++)
        {
            var arrival = legs[i]?.Arrival;
            if (arrival == null) continue;
            var travelDay = plan.LegStart(i + 1);
            if (travelDay < 1) continue;
            if (arrival.Mode == TripTravelModes.Journey)
            {
                if (arrival.JourneyId is not { } journeyId || journeys.ContainsKey(journeyId)) continue;
                journeys[journeyId] = travelDay;
                var cars = Math.Clamp(arrival.Cars ?? 1, 1, JourneyRules.MaxLegs);
                for (var car = 1; car <= cars; car++) map.SegmentAny.Add((travelDay, car));
            }
            else
            {
                map.SegmentAny.Add((travelDay, 0));
                map.SegmentSettled.Add((travelDay, 0));
                map.SegmentOwn.Add((travelDay, 0));
            }
        }

        var live = new List<TripEnquiry>();
        var segments = new List<(TripEnquiry Row, int Day, int Car, bool OnDate)>();
        foreach (var e in enquiries.Where(e => e.Status != TripEnquiryStatuses.Closed))
        {
            if (!TripSegmentRows.IsSegment(e))
            {
                live.Add(e);
                continue;
            }
            var ctx = TripEnquiryContexts.Read(e.ContextJson);
            if (ctx.JourneyId is { } id && journeys.TryGetValue(id, out var day) && ctx.LegIndex is { } car
                && map.SegmentAny.Contains((day, car)))
                segments.Add((e, day, car, start is not { } s || e.CheckIn == TripExecutionRules.NightDate(s, day)));
        }
        map.HasLiveEnquiry = live.Count > 0 || segments.Count > 0;

        // A row on an old travel date (days or start changed) is never settled or open: it counts as red, like the tile.
        foreach (var (e, day, car, _) in segments.Where(x => x.OnDate))
        {
            if (TripBookingStates.IsSettled(e)) map.SegmentSettled.Add((day, car));
            if (TripBookingStates.IsOpenForCompletion(e)) map.HasOpen = true;
        }
        foreach (var (e, day, car, onDate) in segments)
            if ((!onDate || IsRed(e)) && !map.SegmentSettled.Contains((day, car))) map.HasUnreplacedRed = true;

        foreach (var e in live)
        {
            var taxi = e.Kind == TripEnquiryKinds.Taxi;
            var days = DaysOf(e, start, count);
            (taxi ? map.TaxiAny : map.StayAny).UnionWith(days);
            if (TripBookingStates.IsSettled(e))
            {
                if (taxi)
                {
                    map.TaxiOwn.ExceptWith(days);
                    map.TaxiSkipped.ExceptWith(days);
                }
                else
                {
                    map.StayOwn.ExceptWith(days);
                    map.StaySkipped.ExceptWith(days);
                }
                (taxi ? map.TaxiSettled : map.StaySettled).UnionWith(days);
                if (taxi && e.Status == TripEnquiryStatuses.ConfirmedByBoth) map.TaxiBoth.UnionWith(days);
            }
            if (TripBookingStates.IsOpenForCompletion(e))
            {
                map.HasOpen = true;
                if (taxi) map.OpenTaxis++; else map.OpenStays++;
            }
        }

        foreach (var e in live.Where(IsRed))
        {
            var settled = e.Kind == TripEnquiryKinds.Taxi ? map.TaxiSettled : map.StaySettled;
            if (!DaysOf(e, start, count).All(settled.Contains)) map.HasUnreplacedRed = true;
        }
        return map;
    }

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

public static class TripBookingSummary
{
    public static TripBookingCounts Count(TripPlan plan, IReadOnlyList<TripEnquiry> enquiries)
    {
        var map = TripBookingMap.Build(plan, enquiries);
        var total = enquiries
            .Where(e => TripEnquiryStatuses.IsBooked(e.Status))
            .GroupBy(e => e.Id)
            .Select(g => g.First())
            .Sum(e => (e.Kind == TripEnquiryKinds.Taxi ? e.AgreedFare : e.AgreedTotal) ?? 0);
        var plainStays = map.StayOwn.Count + map.StaySkipped.Count;
        var plainTaxis = map.TaxiOwn.Count + map.TaxiSkipped.Count;
        return new TripBookingCounts(
            map.StaySettled.Count - plainStays, map.StayAny.Count - plainStays,
            map.TaxiSettled.Count - plainTaxis, map.TaxiAny.Count - plainTaxis,
            total, map.StayOwn.Count, map.TaxiOwn.Count,
            map.SegmentSettled.Count(s => s.Car > 0), map.SegmentAny.Count(s => s.Car > 0));
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
        if (arrival.Mode != TripTravelModes.Journey) return TripTravelStates.Own;
        if (journey == null || journey.IsDeleted || arrival.JourneyId != journey.Id) return TripTravelStates.NotPlanned;
        if (travelDate is { } date && journey.TravelDate != date) return TripTravelStates.DateChanged;

        var cars = journey.Legs.OrderBy(l => l.LegIndex).ToList();
        if (cars.Count == 0) return TripTravelStates.Saved;
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
            if (car.LastKnownStatus == JourneyLegStatuses.NotFree || (asked && mine.Count > 0 && mine.All(IsRed))) missing = true;
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
}

public static class TripReminderRules
{
    public const int NoReplyHours = 24, HostNotConfirmedHours = 24, WindowHours = 48;
    public const int DaysBeforeStart = 3, StartsInHourIst = 9, TodayHourIst = 7, MarkCompleteHourIst = 10, AutoCompleteDays = 7;
    public const int TravelEveHourIst = 18;

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
    public static bool ShouldRemindStartsIn(Trip trip, TripPlan plan, IReadOnlyList<TripEnquiry> enquiries, DateOnly todayIst)
    {
        if (trip.StartDate is not { } start || DaysLeft(start, todayIst) <= 0) return false;
        return TripBookingStates.Compute(trip, plan, enquiries) switch
        {
            TripBookingStates.PlanOnly => trip.DistrictId != null,
            TripBookingStates.Confirming => !AllSettled(plan, enquiries),
            _ => false,
        };
    }

    // The 3-day push also goes out when Getting there is the only thing left to sort.
    public static bool ShouldSendStartsIn(Trip trip, TripPlan plan, IReadOnlyList<TripEnquiry> enquiries, DateOnly todayIst)
        => trip.StartDate is { } start && DaysLeft(start, todayIst) > 0
           && (ShouldRemindStartsIn(trip, plan, enquiries, todayIst) || TripStepRules.NeedsNudge(plan.Steps));

    public static string StartsInBody(Trip trip, TripPlan plan, IReadOnlyList<TripEnquiry> enquiries, DateOnly todayIst)
        => ShouldRemindStartsIn(trip, plan, enquiries, todayIst) ? StartsInSummary(plan, enquiries) : GettingThereNudge;

    public static bool ProblemAllowed(Trip t, DateOnly todayIst) => t.StartDate is { } s && t.EndDate is { } e && s <= todayIst && todayIst <= e.AddDays(1);

    public static string NoReplyKey(Guid enquiryId) => $"noreply:{enquiryId}";
    public static string NoReplySegmentKey(Guid tripId, DateOnly date) => $"noreply:seg:{tripId:N}:{date:yyyyMMdd}";
    public static string HostNotConfirmedKey(Guid enquiryId) => $"hostnc:{enquiryId}";
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

    // "Stay confirmed. Taxi is booked by you." | "1 stay enquiry waiting. No taxi yet." (+ " Travel booked.") (+ " Getting there not sorted.")
    public static string StartsInSummary(TripPlan plan, IReadOnlyList<TripEnquiry> enquiries)
    {
        var text = BookingSummary(plan, enquiries);
        var travel = TripSummaryLegs.TravelSentence(plan, enquiries);
        text = travel == null ? text : $"{text} {travel}";
        return TripStepRules.NeedsNudge(plan.Steps) ? $"{text} {GettingThereNudge}" : text;
    }

    private static string BookingSummary(TripPlan plan, IReadOnlyList<TripEnquiry> enquiries)
    {
        var map = TripBookingMap.Build(plan, enquiries);
        string stay;
        if (map.OpenStays > 0) stay = $"{map.OpenStays} stay {(map.OpenStays == 1 ? "enquiry" : "enquiries")} waiting.";
        else if (map.StayAny.Count > 0 && map.StayAny.IsSubsetOf(map.StaySettled)) stay = "Stay confirmed.";
        else stay = "No stay yet.";

        // A "No taxi this day" day is settled but is not a taxi.
        var taxiDays = new HashSet<int>(map.TaxiAny);
        taxiDays.ExceptWith(map.TaxiSkipped);
        string taxi;
        if (map.OpenTaxis > 0) taxi = $"{map.OpenTaxis} taxi {(map.OpenTaxis == 1 ? "enquiry" : "enquiries")} waiting.";
        else if (taxiDays.Count > 0 && taxiDays.IsSubsetOf(map.TaxiSettled))
            taxi = taxiDays.IsSubsetOf(map.TaxiBoth) ? "Taxi confirmed." : "Taxi is booked by you.";
        else taxi = "No taxi yet.";
        return $"{stay} {taxi}";
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

    // Same rule as Compute's Ready: every night or day with a choice or enquiry is settled (own and skipped included);
    // a day with no choice at all counts as settled; nothing open or unreplaced red.
    public static bool AllSettled(TripPlan plan, IReadOnlyList<TripEnquiry> enquiries)
    {
        var map = TripBookingMap.Build(plan, enquiries);
        if (map.HasOpen || map.HasUnreplacedRed) return false;
        return map.StayAny.IsSubsetOf(map.StaySettled) && map.TaxiAny.IsSubsetOf(map.TaxiSettled)
            && map.SegmentAny.IsSubsetOf(map.SegmentSettled);
    }
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

        return (title, Body(arrival, travelDate, enquiries, journey, taxis));
    }

    private static string Body(
        TripTravel? arrival, DateOnly travelDate, IReadOnlyList<TripEnquiry> enquiries,
        SavedJourney? journey, IReadOnlyDictionary<Guid, JourneyTaxiInfo> taxis)
    {
        if (arrival == null) return "No travel planned yet. Open the trip.";

        if (arrival.Mode != TripTravelModes.Journey)
        {
            var how = HowText(arrival.How);
            var leaves = TripEnquiryText.TimeText(arrival.LeavesAt);
            return $"Your own travel{(how == null ? "" : " · " + how)}{(leaves == null ? "" : " · " + leaves)}. Open the trip.";
        }

        if (journey == null || journey.IsDeleted || arrival.JourneyId != journey.Id || journey.TravelDate != travelDate)
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

    // "bus" -> "Bus"; "other" and unknown blanks are omitted.
    private static string? HowText(string? how)
    {
        var text = how?.Trim();
        if (string.IsNullOrEmpty(text) || string.Equals(text, TripTravelHows.Other, StringComparison.OrdinalIgnoreCase)) return null;
        return char.ToUpperInvariant(text[0]) + text[1..].ToLowerInvariant();
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
        ShrinkPhoneBooking(e, kept);
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
        NeedsEchoedJson = accepted.NeedsEchoedJson,
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

    private static void ShrinkPhoneBooking(TripEnquiry e, int keptNights)
    {
        if (string.IsNullOrWhiteSpace(e.PhoneBookingJson)) return;
        try
        {
            var booking = JsonSerializer.Deserialize<TripPhoneBooking>(e.PhoneBookingJson, TripJson.Options);
            if (booking == null) return;
            booking.Nights = keptNights;
            if (e.AgreedTotal is { } total) booking.AgreedTotal = total;
            e.PhoneBookingJson = JsonSerializer.Serialize(booking, TripJson.Options);
        }
        catch (JsonException) { }
    }

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
