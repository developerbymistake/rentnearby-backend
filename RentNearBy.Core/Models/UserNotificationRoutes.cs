namespace RentNearBy.Core.Models;

public static class UserNotificationRoutes
{
    public const string MyStays = "/my-stays";
    public const string MyTaxi = "/my-taxi";
    public const string Notifications = "/notifications";
    public const string JobDetail = "/job-detail";
    public const string MyJobs = "/my-jobs";
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

    // Owner roles (AU-15). /my-taxi with { section: "routes" } and /main with { tab: "home" } reuse existing route names.
    public const string StayCalendar = "/stay-calendar";
    public const string StaySpecialPrices = "/stay-special-prices";
    public const string StayPlanVisible = "/stay-plan-visible";
    public const string TaxiPlanVisible = "/taxi-plan-visible";
    public const string TaxiRoutesAndFares = MyTaxi;
    public const string OwnerHome = "/main";
    public const string TripSettle = "/trip-settle";
    public const string TripReplies = "/trip-replies";
    public const string ProfilePhoto = "/profile-photo";
    public const string StayUpcoming = "/stay-upcoming";
    public const string TaxiUpcoming = "/taxi-upcoming";

    // Bakhli social (contract 5).
    public const string People = "/people";
    public const string Traveller = "/traveller";
    public const string JoinRequests = "/join-requests";
    public const string TripPost = "/trip-post";
    public const string TravelBuddies = "/travel-buddies";
    public const string DmRoom = "/dm-room";
    // Bakhli Plus screen (the end notice of PLAN_STATES_SOCIAL 2).
    public const string SeekerPlus = "/seeker-plus";
    // Travel levels page (the level-up congratulation, spec C). No arguments.
    public const string TravelLevels = "/travel-levels";

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
        ListingKinds.Stay => [MyStays, StayEnquiries, StayCalendar, StaySpecialPrices, StayPlanVisible, StayUpcoming],
        ListingKinds.Taxi => [MyTaxi, TripLeads, .. TaxiDriverRoutes, TaxiPlanVisible, TaxiUpcoming],
        _ => [],
    };
}
