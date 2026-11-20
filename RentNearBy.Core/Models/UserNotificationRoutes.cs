namespace RentNearBy.Core.Models;

public static class UserNotificationRoutes
{
    public const string MyStays = "/my-stays";
    public const string MyTaxi = "/my-taxi";
    public const string Notifications = "/notifications";
    public const string JobDetail = "/job-detail";
    public const string Plans = "/plans";
    public const string TripPlan = "/trip-plan";
    public const string StayEnquiries = "/stay-enquiries";
    public const string ChatConversation = "/chat-conversation";
    public const string TripLeads = "/trip-leads";
    public const string MyTrips = "/my-trips";
    public const string TripTaxiReplies = "/trip-taxi-replies";

    public const string TripName = "/trip-name";
    public const string TripCustomize = "/trip-customize";
    public const string TripOverview = "/trip-overview";
    public const string TripTaxiRequest = "/trip-taxi-request";
    public const string TripTaxiStatus = "/trip-taxi-status";
    public const string TaxiRequestRing = "/taxi-request-ring";
    public const string TaxiRequests = "/taxi-requests";
    public const string TaxiRequestDetail = "/taxi-request-detail";
    public const string TaxiBookings = "/taxi-bookings";
    public const string TaxiRating = "/taxi-rating";
    public const string TaxiReady = "/taxi-ready";

    public const string Buddies = "/buddies";
    public const string TripInvite = "/trip-invite";
    public const string TripTravellers = "/trip-travellers";
    public const string TripView = "/trip-view";
    public const string TripExpenses = "/trip-expenses";

    public static readonly IReadOnlyList<string> TaxiDriverRoutes = [TaxiRequestRing, TaxiRequests, TaxiRequestDetail, TaxiBookings, TaxiRating, TaxiReady];
    public static readonly IReadOnlyList<string> TravellerTaxiRoutes = [TripName, TripCustomize, TripOverview, TripTaxiRequest, TripTaxiStatus];

    public static string ForListingKind(string listingKind) => listingKind switch
    {
        ListingKinds.Stay => MyStays,
        ListingKinds.Taxi => MyTaxi,
        _ => Notifications,
    };

    // Inbox routes a role change deletes for the kind being left: its listing screen plus its trip owner screen.
    public static IReadOnlyList<string> OwnedByKind(string listingKind) => listingKind switch
    {
        ListingKinds.Stay => [MyStays, StayEnquiries],
        ListingKinds.Taxi => [MyTaxi, TripLeads, .. TaxiDriverRoutes],
        _ => [],
    };
}
