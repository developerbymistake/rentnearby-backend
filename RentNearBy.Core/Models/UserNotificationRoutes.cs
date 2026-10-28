namespace RentNearBy.Core.Models;

public static class UserNotificationRoutes
{
    public const string MyListings = "/my-listings";
    public const string MyPlots = "/my-plots";
    public const string Notifications = "/notifications";

    public static string ForListingKind(string listingKind) =>
        listingKind == ListingKinds.Room ? MyListings : MyPlots;
}
