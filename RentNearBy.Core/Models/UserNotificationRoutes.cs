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
        ListingKinds.Taxi => [MyTaxi, TripLeads],
        _ => [],
    };
}
