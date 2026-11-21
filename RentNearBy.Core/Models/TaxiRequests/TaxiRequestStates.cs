namespace RentNearBy.Core.Models.TaxiRequests;

public static class RequestStates
{
    public const string Open = "Open";
    public const string Collecting = "Collecting";
    public const string Selected = "Selected";
    public const string Booked = "Booked";
    public const string InProgress = "InProgress";
    public const string Completed = "Completed";
    public const string Cancelled = "Cancelled";
    public const string Expired = "Expired";

    public static readonly IReadOnlyList<string> All = [Open, Collecting, Selected, Booked, InProgress, Completed, Cancelled, Expired];
    public static readonly IReadOnlyList<string> Active = [Open, Collecting, Selected, Booked, InProgress];
    public static readonly IReadOnlyList<string> Final = [Completed, Cancelled, Expired];
    // Still looking for or choosing a driver (the matcher's pool).
    public static readonly IReadOnlyList<string> Dispatching = [Open, Collecting];

    public static readonly string SqlList = TaxiSql.List(All);
    public static readonly string ActiveSqlList = TaxiSql.List(Active);

    public static bool IsActive(string state) => Active.Contains(state);
    public static bool IsFinal(string state) => Final.Contains(state);
}

public static class RequestViews
{
    public const string Finding = "Finding";
    public const string FindingWaiting = "FindingWaiting";
    public const string NoReply = "NoReply";
    public const string Collecting = "Collecting";
    public const string Selected = "Selected";
    public const string Booked = "Booked";
    public const string InProgress = "InProgress";
    public const string Rate = "Rate";
    public const string Done = "Done";
    public const string Busy = "Busy";
    public const string Ended = "Ended";

    public static readonly IReadOnlyList<string> All = [Finding, FindingWaiting, NoReply, Collecting, Selected, Booked, InProgress, Rate, Done, Busy, Ended];
    public static readonly string SqlList = TaxiSql.List(All);
}

public static class OfferStates
{
    public const string Ringing = "Ringing";
    public const string Accepted = "Accepted";
    public const string Declined = "Declined";
    public const string Missed = "Missed";
    public const string Selected = "Selected";
    public const string NotSelected = "NotSelected";
    public const string NotAgreed = "NotAgreed";
    public const string Confirmed = "Confirmed";
    public const string Withdrawn = "Withdrawn";
    public const string Closed = "Closed";

    public static readonly IReadOnlyList<string> All = [Ringing, Accepted, Declined, Missed, Selected, NotSelected, NotAgreed, Confirmed, Withdrawn, Closed];
    // Offers the traveller sees in `drivers` (driver contact is shown only for these).
    public static readonly IReadOnlyList<string> TravellerVisible = [Accepted, Selected, Confirmed];
    public static readonly IReadOnlyList<string> Live = [Ringing, Accepted, Selected];

    public static readonly string SqlList = TaxiSql.List(All);
}

public static class OfferBuckets
{
    public const string New = "New";
    public const string Booked = "Booked";
    public const string Earlier = "Earlier";

    public static readonly IReadOnlyList<string> All = [New, Booked, Earlier];
    public static readonly string SqlList = TaxiSql.List(All);

    // Declined offers are not listed (null). A Confirmed offer is Booked while its booking is active, else Earlier.
    public static string? For(string offerState, bool bookingActive) => offerState switch
    {
        OfferStates.Ringing or OfferStates.Accepted or OfferStates.Selected => New,
        OfferStates.Confirmed => bookingActive ? Booked : Earlier,
        OfferStates.Missed or OfferStates.NotSelected or OfferStates.NotAgreed or OfferStates.Closed or OfferStates.Withdrawn => Earlier,
        _ => null,
    };
}

public static class BookingStates
{
    public const string Booked = "Booked";
    public const string InProgress = "InProgress";
    public const string Completed = "Completed";
    public const string Cancelled = "Cancelled";

    public static readonly IReadOnlyList<string> All = [Booked, InProgress, Completed, Cancelled];
    public static readonly IReadOnlyList<string> Active = [Booked, InProgress];

    public static readonly string SqlList = TaxiSql.List(All);
    public static readonly string ActiveSqlList = TaxiSql.List(Active);
}

public static class BookingCancelledBy
{
    public const string Traveller = "Traveller";
    public const string Driver = "Driver";
    public const string Admin = "Admin";
    public const string System = "System";

    public static readonly IReadOnlyList<string> All = [Traveller, Driver, Admin, System];
    public static readonly string SqlList = TaxiSql.List(All);
}

public static class BookingCompletedBy
{
    public const string Auto = "Auto";
    public const string Traveller = "Traveller";
    public const string Driver = "Driver";

    public static readonly IReadOnlyList<string> All = [Auto, Traveller, Driver];
    public static readonly string SqlList = TaxiSql.List(All);
}

public static class RequestEndReasons
{
    // Cancelled
    public const string TravellerCancelled = "TravellerCancelled";
    public const string PickupChanged = "PickupChanged";
    public const string BookingCancelledByTraveller = "BookingCancelledByTraveller";
    public const string DriverLeftTrip = "DriverLeftTrip";
    public const string AdminCancelled = "AdminCancelled";
    public const string AccountClosed = "AccountClosed";
    // Expired
    public const string AllDriversBusy = "AllDriversBusy";
    public const string NotBooked = "NotBooked";
    // The queue ran out before any driver was ever rung for the request.
    public const string NoReadyDriver = "NoReadyDriver";

    public static readonly IReadOnlyList<string> Cancelled = [TravellerCancelled, PickupChanged, BookingCancelledByTraveller, DriverLeftTrip, AdminCancelled, AccountClosed];
    public static readonly IReadOnlyList<string> Expired = [AllDriversBusy, NotBooked, NoReadyDriver];

    // A queue that ran out (no candidate left, none ringing or chosen): busy if a driver was ever rung, else nobody was ready.
    public static string WhenQueueRunsOut(int offersEver) => offersEver == 0 ? NoReadyDriver : AllDriversBusy;
    public static readonly IReadOnlyList<string> All = [.. Cancelled, .. Expired];
    // The two reasons the traveller may send on cancel (T5).
    public static readonly IReadOnlyList<string> TravellerChoices = [TravellerCancelled, PickupChanged];

    public static readonly string SqlList = TaxiSql.List(All);
}

public static class OfferEndReasons
{
    public const string Expired = "Expired";
    public const string Filled = "Filled";
    public const string ConfirmTimeout = "ConfirmTimeout";
    public const string DeclinedBooking = "DeclinedBooking";
    public const string RequestCancelled = "RequestCancelled";
    public const string RequestExpired = "RequestExpired";
    public const string Booked = "Booked";
    public const string DriverOffline = "DriverOffline";
    public const string ListingRemoved = "ListingRemoved";
    // O12: the driver removed the route the request matched through.
    public const string RouteRemoved = "RouteRemoved";
    // Owner roles: a route hidden by the plan (7.2) and a live taxi sent back to review (AU-6). Never a missed ring.
    public const string RoutePausedByPlan = "RoutePausedByPlan";
    public const string ListingInReview = "ListingInReview";

    public static readonly IReadOnlyList<string> All =
    [
        Expired, Filled, ConfirmTimeout, DeclinedBooking, RequestCancelled, RequestExpired, Booked, DriverOffline, ListingRemoved, RouteRemoved,
        RoutePausedByPlan, ListingInReview,
    ];
    public static readonly string SqlList = TaxiSql.List(All);

    // Body of the visible ring end push (contract 10.2); null = no ring end push for this reason.
    public static string? RingEndBody(string reason) => reason switch
    {
        Filled or Booked => "Taken by another driver",
        RequestCancelled => "The traveller cancelled the request",
        DriverOffline => "You are offline",
        RouteRemoved => "You removed this route",
        RingEndReasons.Accepted => "You accepted this request",
        RingEndReasons.Declined => "You declined this request",
        RingEndReasons.Closed => "This request is closed",
        _ => null,
    };
}

// Ring end push reasons only, never stored as an offer end reason: the driver answered on one device, so the ring stops
// on his other devices (O11 + O14). The answering device never gets it: its token is left out of the send.
public static class RingEndReasons
{
    public const string Accepted = "Accepted";
    public const string Declined = "Declined";
    public const string Closed = "Closed";

    public const int MaxTokenLength = 1024;

    // A reason without a ring end body (ListingRemoved) still has to stop the ring on the other devices.
    public static string ForOtherDevices(string reason) => OfferEndReasons.RingEndBody(reason) is null ? Closed : reason;

    // Old apps send no token: then the answering device is unknown and no backup ring end is sent.
    public static string? AnsweringToken(string? raw)
    {
        var token = raw?.Trim();
        return string.IsNullOrEmpty(token) || token.Length > MaxTokenLength ? null : token;
    }
}

public static class ReasonChips
{
    public const string ChangedPlan = "ChangedPlan";
    public const string NotReachable = "NotReachable";
    public const string PriceNotAgreed = "PriceNotAgreed";
    public const string VehicleIssue = "VehicleIssue";
    public const string Other = "Other";

    public static readonly IReadOnlyList<string> All = [ChangedPlan, NotReachable, PriceNotAgreed, VehicleIssue, Other];
    public static readonly string SqlList = TaxiSql.List(All);

    public static bool IsValid(string? chip) => chip is not null && All.Contains(chip);
}

public static class PlanLockReasons
{
    public const string TaxiRequestActive = "TaxiRequestActive";
    public const string TaxiBookingActive = "TaxiBookingActive";
    // Decision 14, stay half: a booked stay (ConfirmedByBoth or BookedByTraveller) of the trip.
    public const string StayBookingActive = "StayBookingActive";

    public static readonly IReadOnlyList<string> All = [TaxiRequestActive, TaxiBookingActive, StayBookingActive];
    public static readonly string SqlList = TaxiSql.List(All);

    // null = the request state does not lock the plan.
    public static string? For(string requestState) => requestState switch
    {
        RequestStates.Open or RequestStates.Collecting or RequestStates.Selected => TaxiRequestActive,
        RequestStates.Booked or RequestStates.InProgress => TaxiBookingActive,
        _ => null,
    };

    public static bool IsTaxi(string? reason) => reason is TaxiRequestActive or TaxiBookingActive;

    public static string MessageFor(string reason) => reason switch
    {
        TaxiBookingActive => "Plan is locked while your booking is active. Cancel the booking to edit.",
        StayBookingActive => "Plan is locked while your stay booking is active. Cancel the stay booking to edit.",
        _ => "Plan is locked while your taxi request is active. Cancel the request to edit.",
    };

    public static string DeleteMessage => "Cancel the taxi request first.";

    public static string DeleteMessageFor(string reason) => reason == StayBookingActive ? "Cancel your stay booking first." : DeleteMessage;
}

public static class DueActionKinds
{
    public const string OfferExpire = "OfferExpire";
    public const string ConfirmExpire = "ConfirmExpire";
    public const string RequestWaitExpire = "RequestWaitExpire";
    public const string RequestStaleExpire = "RequestStaleExpire";
    public const string BookingStart = "BookingStart";
    public const string BookingAutoComplete = "BookingAutoComplete";
    // O7: the traveller has accepts but chose nobody; push after 2 h and after 24 h.
    public const string ChooseReminderFirst = "ChooseReminderFirst";
    public const string ChooseReminderSecond = "ChooseReminderSecond";

    public static readonly IReadOnlyList<string> ChooseReminders = [ChooseReminderFirst, ChooseReminderSecond];
    public static readonly IReadOnlyList<string> All =
        [OfferExpire, ConfirmExpire, RequestWaitExpire, RequestStaleExpire, BookingStart, BookingAutoComplete, ChooseReminderFirst, ChooseReminderSecond];
    public static readonly string SqlList = TaxiSql.List(All);
}

public static class DueActionStates
{
    public const string Pending = "Pending";
    public const string Running = "Running";
    public const string Done = "Done";
    public const string Cancelled = "Cancelled";
    public const string Skipped = "Skipped";
    public const string Failed = "Failed";

    public static readonly IReadOnlyList<string> All = [Pending, Running, Done, Cancelled, Skipped, Failed];
    // Every final state sets DoneAt (retention purges on it).
    public static readonly IReadOnlyList<string> Final = [Done, Cancelled, Skipped, Failed];
    public static readonly string SqlList = TaxiSql.List(All);
}

internal static class TaxiSql
{
    public static string List(IEnumerable<string> values) => string.Join(", ", values.Select(v => $"'{v}'"));
}
