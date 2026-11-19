using RentNearBy.Core.DTOs.TaxiRequests;
using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Models.TaxiRequests;

public sealed record TaxiRequestError(int Status, string Type, string Message, object? Data = null);

// What the server knows when it checks a create (contract T2). Pickup and drop keys are the live T1 lists. SightseeingCityKey
// = city of the first chosen sightseeing day (O4; null = the first city) and SightseeingDrivers counts the pool for it.
public sealed record TaxiCreateFacts(
    DateOnly? TripStart,
    int DayCount,
    bool TripCompleted,
    DateOnly TodayIst,
    long FirstCityKey,
    long LastCityKey,
    IReadOnlyCollection<long> PickupKeys,
    IReadOnlyCollection<long> DropKeys,
    int SightseeingDrivers,
    long? SightseeingCityKey = null,
    int Travellers = 1);

// A create that passed every rule: what the request row stores besides the snapshot.
public sealed record TaxiCreatePlan(
    DateOnly TripStart,
    TaxiDateRange Occupied,
    long DispatchCityKey,
    long MatchToCityKey,
    long? MatchFromCityKey,
    long? PickupCityKey,
    long? DropCityKey,
    bool DropSameAsPickup,
    IReadOnlyList<int> SightseeingDays,
    int Seats);

public sealed record TaxiCreateCheck(TaxiCreatePlan? Plan, TaxiRequestError? Error);

// Another trip of the same traveller that already holds a taxi request or booking on overlapping dates (O3 + O10).
public sealed record TaxiOverlap(Guid TripId, string TripName, Guid RequestId, DateOnly DateFrom, DateOnly DateTo);

public sealed record TaxiCancelPlan(string EndReason, string? Chip, string CancelledBy);

public sealed record TaxiCancelCheck(TaxiCancelPlan? Plan, TaxiRequestError? Error, bool AlreadyDone);

// Pure rules of contract T2 (create) and T5 (cancel). The service runs them twice: before the trip lock (fast refusal)
// and again under the lock with the fresh trip row.
public static class TaxiRequestCreateRules
{
    public const string BadRequestType = "BadRequest";
    public const string SeatsMessage = "Choose 4, 6, 7 or 12 seats";
    public const string ReasonMessage = "Choose why you cancel";
    public const string ChipMessage = "Choose a valid reason";
    public const string TripCompletedMessage = "This trip is complete. Plan changes are off.";

    public static TaxiCreateCheck Check(CreateTaxiRequestDto body, TaxiCreateFacts facts)
    {
        var pickupOn = body.Pickup?.On == true;
        var sightseeingOn = body.Sightseeing?.On == true;
        var dropOn = body.Drop?.On == true;

        if (!pickupOn && !sightseeingOn && !dropOn) return Fail(400, TaxiRequestErrorTypes.TaxiSectionsRequired);
        if (!TaxiRequestSeats.IsValid(body.Seats)) return Fail(400, BadRequestType, SeatsMessage);
        if (facts.Travellers > TaxiRequestSeats.MaxTravellers) return Fail(400, TaxiRequestErrorTypes.TooManyTravellers);
        if (!TaxiRequestSeats.Holds(body.Seats, facts.Travellers)) return Fail(400, TaxiRequestErrorTypes.SeatsTooFew);
        if (facts.TripCompleted) return Fail(409, TripEnquiryErrorTypes.TripCompleted, TripCompletedMessage);
        if (facts.TripStart is not { } start || facts.DayCount < 1) return Fail(409, TaxiRequestErrorTypes.TripDatesRequired);

        var days = sightseeingOn ? (body.Sightseeing!.Days ?? []).Distinct().Order().ToList() : [];
        if (sightseeingOn && (days.Count == 0 || days.Any(d => d < 1 || d > facts.DayCount || DateOf(start, d) < facts.TodayIst)))
            return Fail(400, TaxiRequestErrorTypes.SightseeingDaysInvalid);

        var sameAsPickup = dropOn && body.Drop!.SameAsPickup;
        if (sameAsPickup && !pickupOn) return Fail(409, TaxiRequestErrorTypes.DropNotAvailable);

        if (pickupOn && DateOf(start, 1) < facts.TodayIst) return Fail(409, TaxiRequestErrorTypes.DayInPast);
        if (dropOn && DateOf(start, facts.DayCount) < facts.TodayIst) return Fail(409, TaxiRequestErrorTypes.DayInPast);

        long? pickupKey = null;
        if (pickupOn)
        {
            if (body.Pickup!.CityKey is not { } key || !facts.PickupKeys.Contains(key)) return Fail(409, TaxiRequestErrorTypes.PickupNotAvailable);
            pickupKey = key;
        }

        long? dropKey = null;
        if (dropOn)
        {
            if (sameAsPickup) dropKey = pickupKey;
            else if (body.Drop!.CityKey is { } key && facts.DropKeys.Contains(key)) dropKey = key;
            else return Fail(409, TaxiRequestErrorTypes.DropNotAvailable);
        }

        if (!pickupOn && !dropOn && facts.SightseeingDrivers < 1) return Fail(409, TaxiRequestErrorTypes.NoDriversNearHere);

        var occupied = TaxiDateRange.Occupied(pickupOn, sightseeingOn, dropOn, start, facts.DayCount, days)
            ?? throw new InvalidOperationException("A checked create always occupies a range");
        var (dispatch, matchTo, matchFrom) = DispatchKeys(pickupKey, dropKey, facts.FirstCityKey, facts.LastCityKey,
            facts.SightseeingCityKey ?? facts.FirstCityKey);
        return new TaxiCreateCheck(new TaxiCreatePlan(start, occupied, dispatch, matchTo, matchFrom, pickupKey, dropKey, sameAsPickup, days, body.Seats), null);
    }

    // MASTER_PLAN 2.2 and P18. Pickup = routes from the pickup city to the first city; drop without pickup = routes from
    // the drop city to the last city; sightseeing only = any route to the sightseeing city (O4). The pool key follows the same order.
    public static (long Dispatch, long MatchTo, long? MatchFrom) DispatchKeys(long? pickupKey, long? dropKey, long firstCityKey, long lastCityKey,
        long sightseeingCityKey)
    {
        if (pickupKey is { } p) return (p, firstCityKey, p);
        if (dropKey is { } d) return (d, lastCityKey, d);
        return (sightseeingCityKey, sightseeingCityKey, null);
    }

    // O4: the city of the first chosen sightseeing day; no day or no legs = the first city.
    public static long SightseeingCityOf(TripPlan plan, IEnumerable<int>? days, long firstCityKey)
    {
        var chosen = days?.Where(d => d >= 1).ToList();
        if (chosen is not { Count: > 0 }) return firstCityKey;
        return plan.LegOf(chosen.Min())?.CityKey is { } key and > 0 ? key : firstCityKey;
    }

    // O4 for T1: each plan city with its days, in day order (one entry for a one-city trip).
    public static IReadOnlyList<(long CityKey, string Name, List<int> Days)> SightseeingCityDays(TripPlan plan, long firstCityKey)
    {
        var days = plan.Days?.Where(d => d != null).Select(d => d.Day).Distinct().Order().ToList() ?? [];
        var cities = new List<(long CityKey, string Name, List<int> Days)>();
        foreach (var day in days)
        {
            var leg = plan.LegOf(day);
            var key = leg?.CityKey is { } k and > 0 ? k : firstCityKey;
            var name = leg?.CityName is { Length: > 0 } n ? n : plan.CityName;
            var at = cities.FindIndex(c => c.CityKey == key);
            if (at < 0) cities.Add((key, name, [day]));
            else cities[at].Days.Add(day);
        }
        return cities;
    }

    public static DateOnly DateOf(DateOnly tripStart, int day) => tripStart.AddDays(day - 1);

    // Plan people are optional; a request always has at least one traveller.
    public static int TravellersOf(TripSettings? settings)
        => Math.Clamp((settings?.Adults ?? 0) + (settings?.Kids ?? 0), 1, short.MaxValue);

    // P25: an open local taxi ask or booking from the old routes blocks a new request. Journey cars never block.
    public static LegacyTaxiActiveData? LegacyBlock(IEnumerable<TripEnquiry> rows)
    {
        var open = rows
            .Where(e => e.Kind == TripEnquiryKinds.Taxi && e.TripId != null && TripEnquiryStatuses.Open.Contains(e.Status) && !TripSegmentRows.IsSegment(e))
            .Select(e => new LegacyTaxiEnquiryDto { Id = e.Id, Booked = TripEnquiryStatuses.Booked.Contains(e.Status) })
            .ToList();
        return open.Count == 0 ? null : new LegacyTaxiActiveData { Enquiries = open };
    }

    public static TaxiRequestError OverlapError(TaxiOverlap overlap)
    {
        var name = string.IsNullOrWhiteSpace(overlap.TripName) ? "another trip" : overlap.TripName.Trim();
        return new(409, TaxiRequestErrorTypes.OverlappingTaxiRequest, TaxiRequestErrorTypes.OverlappingTaxiRequestMessage(name),
            new OverlappingTaxiRequestData
            {
                TripId = overlap.TripId,
                TripName = name,
                RequestId = overlap.RequestId,
                DateFrom = overlap.DateFrom,
                DateTo = overlap.DateTo,
            });
    }

    public static TaxiRequestError LegacyError(LegacyTaxiActiveData data)
        => new(409, TaxiRequestErrorTypes.LegacyTaxiActive, TaxiRequestErrorTypes.Messages[TaxiRequestErrorTypes.LegacyTaxiActive], data);

    // T5. AlreadyDone = the request is already cancelled: the repeat answers 200 with the current object.
    public static TaxiCancelCheck CancelCheck(string state, string? reason, string? chip)
    {
        if (reason is null || !RequestEndReasons.TravellerChoices.Contains(reason)) return CancelFail(400, BadRequestType, ReasonMessage);
        if (chip is not null && !ReasonChips.IsValid(chip)) return CancelFail(400, BadRequestType, ChipMessage);
        switch (state)
        {
            case RequestStates.Cancelled:
                return new TaxiCancelCheck(null, null, true);
            case RequestStates.Open or RequestStates.Collecting or RequestStates.Selected:
                return new TaxiCancelCheck(new TaxiCancelPlan(reason, null, BookingCancelledBy.Traveller), null, false);
            case RequestStates.Booked:
                return chip is null
                    ? CancelFail(400, TaxiRequestErrorTypes.ReasonRequired)
                    : new TaxiCancelCheck(new TaxiCancelPlan(RequestEndReasons.BookingCancelledByTraveller, chip, BookingCancelledBy.Traveller), null, false);
            default:
                return CancelFail(409, TaxiRequestErrorTypes.TaxiRequestStateInvalid);
        }
    }

    public const int AdminNoteMaxLength = 300;

    // AD4: the trimmed note is 1 to 300 characters. Note = the text to store; Error = 400 BadRequest.
    public static (string? Note, TaxiRequestError? Error) AdminNoteCheck(string? note)
    {
        var text = note?.Trim() ?? "";
        return text.Length is < 1 or > AdminNoteMaxLength
            ? (null, Error(400, BadRequestType, $"Note must be 1 to {AdminNoteMaxLength} characters"))
            : (text, null);
    }

    public static TaxiRequestError Error(int status, string type, string? message = null, object? data = null)
        => new(status, type, message ?? TaxiRequestErrorTypes.Messages.GetValueOrDefault(type) ?? type, data);

    private static TaxiCreateCheck Fail(int status, string type, string? message = null) => new(null, Error(status, type, message));

    private static TaxiCancelCheck CancelFail(int status, string type, string? message = null) => new(null, Error(status, type, message), false);
}
