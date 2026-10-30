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
}
