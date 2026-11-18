namespace RentNearBy.Core.Models.TaxiRequests;

// HTTP route templates of the contract (sections 3, 5, 6, 7, 11) and the SignalR names (section 9).
public static class TaxiRequestRoutes
{
    public const string TripPickupPlaces = "/trips/{tripId}/taxi-request/pickup-places";
    public const string TripTaxiRequest = "/trips/{tripId}/taxi-request";
    public const string TripInvite = "/trips/{id}/invite";
    public const string Request = "/taxi-requests/{requestId}";
    public const string RequestCancel = "/taxi-requests/{requestId}/cancel";
    public const string RequestAskMore = "/taxi-requests/{requestId}/ask-more";
    public const string RequestSelect = "/taxi-requests/{requestId}/offers/{offerId}/select";
    public const string RequestNotAgreed = "/taxi-requests/{requestId}/offers/{offerId}/not-agreed";
    public const string RequestEnd = "/taxi-requests/{requestId}/end";
    public const string RequestRating = "/taxi-requests/{requestId}/rating";

    public const string DriverGroup = "/taxi-requests/driver";
    public const string DriverOffers = "/offers";
    public const string DriverOffer = "/offers/{offerId}";
    public const string DriverAccept = "/offers/{offerId}/accept";
    public const string DriverDecline = "/offers/{offerId}/decline";
    public const string DriverConfirm = "/offers/{offerId}/confirm";
    public const string DriverWithdraw = "/offers/{offerId}/withdraw";
    public const string DriverBookingEnd = "/bookings/{bookingId}/end";
    public const string DriverBookings = "/bookings";
    public const string DriverRating = "/rating";
    public const string DriverReadiness = "/readiness";
    public const string DriverTestRing = "/test-ring";
    public const string OfferAck = "/taxi-requests/offers/{offerId}/ack";

    public const string AdminRequests = "/admin/taxi-requests";
    public const string AdminRequest = "/admin/taxi-requests/{id}";
    public const string AdminRequestCancel = "/admin/taxi-requests/{id}/cancel";
    public const string AdminBookings = "/admin/taxi-bookings";
    public const string AdminHealth = "/admin/taxi-requests/health";

    public const string HubSubscribe = "SubscribeTaxiRequest";
    public const string HubUnsubscribe = "UnsubscribeTaxiRequest";
    public const string HubEvent = "TaxiRequestChanged";
    public const string HubGroupPrefix = "taxi_request_";

    public static string HubGroup(Guid requestId) => HubGroupPrefix + requestId.ToString("D");
}

// FCM payload names of the contract (section 10).
public static class TaxiPushKinds
{
    public const string Ring = "taxi_ring";
    public const string RingTest = "taxi_ring_test";
    public const string RingEnd = "taxi_ring_end";
    public const string PayloadVersion = "1";
    public const string RingChannelId = "taxi_requests_ring_v1";
    public const string UpdatesChannelId = "taxi_request_updates_v1";

    public static string RingTag(Guid offerId) => "taxi_ring_" + offerId.ToString("D");
    public static string RingKey(Guid offerId) => "ring:" + offerId.ToString("D");
    public static string RingEndKey(Guid offerId) => "ringend:" + offerId.ToString("D");
}
