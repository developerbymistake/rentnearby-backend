namespace RentNearBy.Core.Models.TaxiRequests;

// The few facts that choose a variant of the copy; everything else comes from the notice itself.
public sealed record TaxiNoticeDetail(string? Reason = null, bool DuringTrip = false, bool ToDriver = false, int Count = 0);

public sealed record TaxiNoticeCopy(string Type, string Title, string Body, string Route, string ArgsJson);

// The words, route and args of the 12 visible notices (contract table 10.3) and the choose reminder (O7).
public static class TaxiRequestNotifications
{
    public static readonly IReadOnlyList<string> Types =
    [
        NotificationTypes.TaxiDriverAccepted, NotificationTypes.TaxiSelectionLapsed, NotificationTypes.TaxiBooked,
        NotificationTypes.TaxiRequestEnded, NotificationTypes.TaxiDriverWithdrew, NotificationTypes.TaxiRateDriver,
        NotificationTypes.TaxiTripEnded, NotificationTypes.TaxiSelected, NotificationTypes.TaxiNotSelected,
        NotificationTypes.TaxiRequestClosed, NotificationTypes.TaxiBookingCancelled, NotificationTypes.TaxiRingsMissed,
        NotificationTypes.TaxiChooseDriver,
    ];

    public static TaxiNoticeCopy Build(string type, Guid tripId, Guid requestId, Guid? offerId = null, string? personName = null, TaxiNoticeDetail? detail = null)
    {
        detail ??= new TaxiNoticeDetail();
        var name = string.IsNullOrWhiteSpace(personName) ? null : personName.Trim();

        string traveller() => TravellerArgs(tripId, requestId);
        TaxiNoticeCopy Make(string title, string body, string route, string args) => new(type, title, body, route, args);
        TaxiNoticeCopy ToTraveller(string title, string body) => Make(title, body, UserNotificationRoutes.TripTaxiStatus, traveller());
        TaxiNoticeCopy ToDriverList(string title, string body, string route) => Make(title, body, route, "{}");

        switch (type)
        {
            case NotificationTypes.TaxiDriverAccepted:
                return ToTraveller("A driver accepted", "Open to call and choose your driver");
            case NotificationTypes.TaxiSelectionLapsed:
                return IsDriverGone(detail.Reason)
                    ? ToTraveller("Driver not available", "This driver is no longer available. Choose another driver.")
                    : ToTraveller("Driver did not confirm", "Choose another driver");
            case NotificationTypes.TaxiBooked:
                return ToTraveller("Taxi booked", $"{name ?? "Your driver"} confirmed your booking");
            case NotificationTypes.TaxiRequestEnded:
                return detail.Reason switch
                {
                    RequestEndReasons.NoReadyDriver => ToTraveller("No driver is ready right now", "Please try again in a little while."),
                    RequestEndReasons.AllDriversBusy or RequestEndReasons.NotBooked => ToTraveller("All drivers are busy right now", "Please try again in a little while."),
                    _ => ToTraveller("Request closed", "You can send a new request"),
                };
            case NotificationTypes.TaxiDriverWithdrew:
                return ToTraveller("Your driver cancelled", detail.DuringTrip ? "You can request another taxi" : "We are finding another driver");
            case NotificationTypes.TaxiRateDriver:
                return ToTraveller("Rate your driver", $"How was your trip with {name ?? "your driver"}?");
            case NotificationTypes.TaxiTripEnded:
                return detail.ToDriver
                    ? ToDriverList("Trip ended", $"{name ?? "The traveller"} ended the trip", UserNotificationRoutes.TaxiBookings)
                    : ToTraveller("Trip ended", $"{name ?? "Your driver"} ended the trip");
            case NotificationTypes.TaxiSelected:
                return Make("Traveller selected you", "Confirm within 15 minutes", UserNotificationRoutes.TaxiRequestDetail,
                    OfferArgs(offerId ?? throw new ArgumentException("TaxiSelected needs an offer id", nameof(offerId))));
            case NotificationTypes.TaxiNotSelected:
                return ToDriverList("Not selected", "The traveller chose another driver", UserNotificationRoutes.TaxiRequests);
            case NotificationTypes.TaxiRequestClosed:
                return ToDriverList("Request closed",
                    IsTravellerCancel(detail.Reason) ? "The traveller cancelled the request" : "This request is closed", UserNotificationRoutes.TaxiRequests);
            case NotificationTypes.TaxiBookingCancelled:
                return ToDriverList("Booking cancelled", "Your dates are free again", UserNotificationRoutes.TaxiBookings);
            case NotificationTypes.TaxiRingsMissed:
                return ToDriverList("You are missing requests", "Open Get ready for requests to fix your phone", UserNotificationRoutes.TaxiReady);
            case NotificationTypes.TaxiChooseDriver:
                return ToTraveller("Choose your driver", ChooseDriverBody(detail.Count));
            default:
                throw new ArgumentOutOfRangeException(nameof(type), type, "Not a taxi request notice");
        }
    }

    public static string ChooseDriverBody(int replied) => replied == 1
        ? "1 driver replied. Choose the driver to confirm your taxi."
        : $"{replied} drivers replied. Choose one to confirm your taxi.";

    // A selection that lapsed because the driver can no longer take it (booked on these dates elsewhere, taxi removed), O8.
    public static bool IsDriverGone(string? offerEndReason)
        => offerEndReason is OfferEndReasons.Booked or OfferEndReasons.ListingRemoved;

    // Ends the traveller chose; expiry, admin cancel and a closed account get the neutral copy.
    public static bool IsTravellerCancel(string? endReason)
        => endReason is RequestEndReasons.TravellerCancelled or RequestEndReasons.PickupChanged or RequestEndReasons.BookingCancelledByTraveller;

    public static string TravellerArgs(Guid tripId, Guid requestId)
        => "{\"tripId\":\"" + tripId.ToString("D") + "\",\"requestId\":\"" + requestId.ToString("D") + "\"}";

    public static string OfferArgs(Guid offerId) => TaxiRingPayload.OfferArgs(offerId);
}
