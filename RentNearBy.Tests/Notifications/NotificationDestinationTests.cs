namespace RentNearBy.Tests.Notifications;

public class NotificationDestinationTests
{
    [Fact]
    public void ToFcmData_NoArgs_RouteOnly()
    {
        var data = new NotificationDestination(AdminNotificationRoutes.Purchases).ToFcmData();

        Assert.Single(data);
        Assert.Equal("/admin/purchases", data["action_route"]);
        Assert.False(data.ContainsKey("action_args_json"));
    }

    [Fact]
    public void ToFcmData_WithArgs_RouteAndArgs()
    {
        const string args = "{\"userId\":null,\"purchaseId\":null,\"kind\":\"ChargebackReview\"}";

        var data = new NotificationDestination(AdminNotificationRoutes.Purchases, args).ToFcmData();

        Assert.Equal(2, data.Count);
        Assert.Equal("/admin/purchases", data["action_route"]);
        Assert.Equal(args, data["action_args_json"]);
    }

    [Theory]
    [InlineData(AdminNotificationRoutes.ReportedListings, "/admin/reports")]
    [InlineData(AdminNotificationRoutes.GoLiveRequests, "/admin/golive-requests")]
    [InlineData(AdminNotificationRoutes.RoleRequests, "/admin/role-requests")]
    [InlineData(AdminNotificationRoutes.StayJobs, "/admin/stay-jobs")]
    [InlineData(AdminNotificationRoutes.Taxis, "/admin/taxis")]
    [InlineData(AdminNotificationRoutes.Purchases, "/admin/purchases")]
    public void AdminRoutes_MatchAdminAppKeys(string route, string expected)
        => Assert.Equal(expected, route);
}
