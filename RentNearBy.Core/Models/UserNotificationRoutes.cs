namespace RentNearBy.Core.Models;

public static class UserNotificationRoutes
{
    public const string MyListings = "/my-listings";
    public const string MyPlots = "/my-plots";
    public const string MyStays = "/my-stays";
    public const string MyTaxi = "/my-taxi";
    public const string Notifications = "/notifications";
    public const string JobDetail = "/job-detail";

    public static string ForListingKind(string listingKind) => listingKind switch
    {
        ListingKinds.Room => MyListings,
        ListingKinds.Stay => MyStays,
        ListingKinds.Taxi => MyTaxi,
        _ => MyPlots,
    };
}
