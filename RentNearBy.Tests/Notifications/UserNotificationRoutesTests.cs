namespace RentNearBy.Tests.Notifications;

public class UserNotificationRoutesTests
{
    [Theory]
    [InlineData(ListingKinds.Stay, "/my-stays")]
    [InlineData(ListingKinds.Taxi, "/my-taxi")]
    [InlineData("Anything", "/notifications")]
    public void ForListingKind_Kind_Route(string kind, string expected)
        => Assert.Equal(expected, UserNotificationRoutes.ForListingKind(kind));

    [Theory]
    [InlineData(ListingKinds.Stay, "/my-stays,/stay-enquiries")]
    [InlineData(ListingKinds.Taxi, "/my-taxi,/trip-leads,/taxi-request-ring,/taxi-requests,/taxi-request-detail,/taxi-bookings,/taxi-rating,/taxi-ready")]
    [InlineData("Anything", "")]
    public void OwnedByKind_Kind_Routes(string kind, string expected)
        => Assert.Equal(expected, string.Join(",", UserNotificationRoutes.OwnedByKind(kind)));

    [Fact]
    public void All_six_driver_routes_are_owned_by_taxi()
    {
        string[] six = ["/taxi-request-ring", "/taxi-requests", "/taxi-request-detail", "/taxi-bookings", "/taxi-rating", "/taxi-ready"];
        Assert.Equal(six, UserNotificationRoutes.TaxiDriverRoutes);
        var owned = UserNotificationRoutes.OwnedByKind(ListingKinds.Taxi);
        Assert.All(six, r => Assert.Contains(r, owned));
        Assert.All(six, r => Assert.DoesNotContain(r, UserNotificationRoutes.OwnedByKind(ListingKinds.Stay)));
    }

    [Fact]
    public void The_five_traveller_routes_are_owned_by_no_kind()
    {
        string[] five = ["/trip-name", "/trip-customize", "/trip-overview", "/trip-taxi-request", "/trip-taxi-status"];
        Assert.Equal(five, UserNotificationRoutes.TravellerTaxiRoutes);
        Assert.All(five, r => Assert.DoesNotContain(r, UserNotificationRoutes.OwnedByKind(ListingKinds.Taxi)));
        Assert.All(five, r => Assert.DoesNotContain(r, UserNotificationRoutes.OwnedByKind(ListingKinds.Stay)));
    }

    [Fact]
    public void The_eleven_new_routes_are_distinct()
        => Assert.Equal(11, UserNotificationRoutes.TaxiDriverRoutes.Concat(UserNotificationRoutes.TravellerTaxiRoutes).Distinct().Count());
}
