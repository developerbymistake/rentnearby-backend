namespace RentNearBy.Tests.Notifications;

public class UserNotificationRoutesTests
{
    [Theory]
    [InlineData(ListingKinds.Room, "/my-listings")]
    [InlineData(ListingKinds.Stay, "/my-stays")]
    [InlineData(ListingKinds.Taxi, "/my-taxi")]
    [InlineData(ListingKinds.Plot, "/my-plots")]
    [InlineData("Anything", "/my-plots")]
    public void ForListingKind_Kind_Route(string kind, string expected)
        => Assert.Equal(expected, UserNotificationRoutes.ForListingKind(kind));

    [Theory]
    [InlineData(ListingKinds.Room, "/my-listings")]
    [InlineData(ListingKinds.Plot, "/my-plots")]
    [InlineData(ListingKinds.Stay, "/my-stays,/stay-enquiries")]
    [InlineData(ListingKinds.Taxi, "/my-taxi,/trip-leads")]
    public void OwnedByKind_Kind_Routes(string kind, string expected)
        => Assert.Equal(expected, string.Join(",", UserNotificationRoutes.OwnedByKind(kind)));
}
