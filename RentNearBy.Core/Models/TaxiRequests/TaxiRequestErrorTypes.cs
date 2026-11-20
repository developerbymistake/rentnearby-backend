namespace RentNearBy.Core.Models.TaxiRequests;

// error.type values of the taxi request contract (section 8). Clients switch on these, never on the message.
public static class TaxiRequestErrorTypes
{
    public const string TripExecutionDisabled = "TripExecutionDisabled";
    public const string PlanLocked = "PlanLocked";
    public const string TripNameInvalid = "TripNameInvalid";
    public const string NotTripOwner = "NotTripOwner";
    public const string TaxiRequestNotFound = "TaxiRequestNotFound";
    public const string TaxiRequestAlreadyActive = "TaxiRequestAlreadyActive";
    public const string TaxiRequestStateInvalid = "TaxiRequestStateInvalid";
    public const string TaxiRequestLimitReached = "TaxiRequestLimitReached";
    public const string LegacyTaxiActive = "LegacyTaxiActive";
    public const string TaxiRequestActive = "TaxiRequestActive";
    public const string TaxiSectionsRequired = "TaxiSectionsRequired";
    public const string PickupNotAvailable = "PickupNotAvailable";
    public const string DropNotAvailable = "DropNotAvailable";
    public const string NoDriversNearHere = "NoDriversNearHere";
    public const string SightseeingDaysInvalid = "SightseeingDaysInvalid";
    public const string TripDatesRequired = "TripDatesRequired";
    public const string DayInPast = "DayInPast";
    public const string AllDriversBusy = "AllDriversBusy";
    // Drivers are listed for the route but none can be rung now (none ring-ready, or none fits seats and dates).
    public const string NoReadyDriver = "NoReadyDriver";
    public const string ReasonRequired = "ReasonRequired";
    public const string OfferNotFound = "OfferNotFound";
    public const string NotOfferDriver = "NotOfferDriver";
    public const string OfferMissed = "OfferMissed";
    public const string OfferStateInvalid = "OfferStateInvalid";
    public const string ConfirmWindowPassed = "ConfirmWindowPassed";
    public const string DatesAlreadyBooked = "DatesAlreadyBooked";
    public const string RatingNotAllowed = "RatingNotAllowed";
    public const string AlreadyRated = "AlreadyRated";
    public const string SeatsTooFew = "SeatsTooFew";
    public const string TooManyTravellers = "TooManyTravellers";
    public const string OverlappingTaxiRequest = "OverlappingTaxiRequest";
    // Existing types the contract reuses on the new routes.
    public const string TooManyRequests = "TooManyRequests";
    public const string NotFound = "NotFound";
    public const string DayAlreadyBooked = "DayAlreadyBooked";

    public static readonly IReadOnlyList<string> All =
    [
        TripExecutionDisabled, PlanLocked, TripNameInvalid, NotTripOwner, TaxiRequestNotFound, TaxiRequestAlreadyActive,
        TaxiRequestStateInvalid, TaxiRequestLimitReached, LegacyTaxiActive, TaxiRequestActive, TaxiSectionsRequired,
        PickupNotAvailable, DropNotAvailable, NoDriversNearHere, SightseeingDaysInvalid, TripDatesRequired, DayInPast,
        AllDriversBusy, ReasonRequired, OfferNotFound, NotOfferDriver, OfferMissed, OfferStateInvalid, ConfirmWindowPassed,
        DatesAlreadyBooked, RatingNotAllowed, AlreadyRated, TooManyRequests, NotFound, DayAlreadyBooked,
        SeatsTooFew, TooManyTravellers, OverlappingTaxiRequest, NoReadyDriver,
    ];

    // Messages the contract fixes (English, shown as is when the app has no own copy).
    public static readonly IReadOnlyDictionary<string, string> Messages = new Dictionary<string, string>
    {
        [TripExecutionDisabled] = "Trip bookings are paused.",
        [TripNameInvalid] = "Trip name must be 1 to 60 characters",
        [NotTripOwner] = "You cannot change this trip",
        [TaxiRequestNotFound] = "Request not found",
        [TaxiRequestAlreadyActive] = "You already have a taxi request for this trip",
        [TaxiRequestStateInvalid] = "This request has changed. Please check again.",
        [TaxiRequestLimitReached] = "Too many requests. Please try again later.",
        [LegacyTaxiActive] = "This trip already has a taxi ask from an older app version. Cancel it before you send a new request.",
        [TaxiRequestActive] = "This trip already has a taxi request. Update the app to see it.",
        [TaxiSectionsRequired] = "Turn on at least one section",
        [PickupNotAvailable] = "No pickup available here",
        [DropNotAvailable] = "No drop available here",
        [NoDriversNearHere] = "No taxi drivers registered near here yet",
        [SightseeingDaysInvalid] = "Choose the days you need a taxi",
        [TripDatesRequired] = "Add your trip dates first",
        [DayInPast] = "That day has passed",
        [AllDriversBusy] = "All drivers are busy right now. Please try again in a little while.",
        [NoReadyDriver] = "No driver is ready for this route right now. Please try again in a little while.",
        [ReasonRequired] = "Choose a reason",
        [OfferNotFound] = "Request not found",
        [NotOfferDriver] = "You cannot answer this request",
        [OfferMissed] = "This request went to the next drivers",
        [OfferStateInvalid] = "This request has changed. Please check again.",
        [ConfirmWindowPassed] = "Time to confirm is over",
        [RatingNotAllowed] = "You can rate after the trip",
        [AlreadyRated] = "You already rated this driver",
        [SeatsTooFew] = "Choose a taxi with a seat for everyone",
        [TooManyTravellers] = "For groups above 12, please send separate requests.",
        [OverlappingTaxiRequest] = "You already have a taxi for these dates in another trip",
    };

    public static string OverlappingTaxiRequestMessage(string tripName)
        => $"You already have a taxi request for these dates in {tripName}";

    public static string DatesAlreadyBookedMessage(DateOnly from, DateOnly to)
        => $"Dates already booked. You have another booking from {from.ToString("d MMM", System.Globalization.CultureInfo.InvariantCulture)} to {to.ToString("d MMM", System.Globalization.CultureInfo.InvariantCulture)}.";
}
