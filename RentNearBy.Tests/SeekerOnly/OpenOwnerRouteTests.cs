using static RentNearBy.Tests.SeekerOnly.SeekerRouteTable;

namespace RentNearBy.Tests.SeekerOnly;

// BE-T4 (SPEC R7): the routes Stay Owners and Taxi Drivers really use stay open for them after the seeker gate.
public class OpenOwnerRouteTests
{
    private static async Task AssertOpenForAsync(IEnumerable<string> keys, params string[] roles)
    {
        foreach (var key in keys)
        {
            var route = Route(key);
            Assert.DoesNotContain(AuthPolicies.SeekerOnly, route.Policies);
            foreach (var role in roles)
                Assert.True(await AllowsAsync(route, SeekerRouteTable.User(role)), $"{key}: {role} was refused");
        }
    }

    [Fact]
    public async Task Host_enquiry_list_needs_the_stay_owner_policy()
    {
        var route = Route("GET /api/v1/trips/enquiries/owner");
        Assert.Equal(["CanListStays"], route.Policies);
        Assert.True(await AllowsAsync(route, SeekerRouteTable.User(UserRoles.StayOwner)));
        Assert.False(await AllowsAsync(route, SeekerRouteTable.User(UserRoles.Seeker)));
    }

    [Fact]
    public Task Host_actions_stay_open() => AssertOpenForAsync(
    [
        "POST /api/v1/trips/enquiries/{id:guid}/offers",
        "POST /api/v1/trips/enquiries/{id:guid}/not-available",
        "POST /api/v1/trips/enquiries/{id:guid}/share-details",
        "POST /api/v1/trips/enquiries/{id:guid}/revised-offer",
        "POST /api/v1/trips/enquiries/{id:guid}/change-request/{offerId:guid}/accept",
        "POST /api/v1/trips/enquiries/{id:guid}/report-traveller",
    ], UserRoles.StayOwner, UserRoles.TaxiDriver);

    [Fact]
    public async Task Driver_leads_stay_open()
    {
        var list = Route("GET /api/v1/trips/leads/driver");
        Assert.Equal(["CanListTaxis"], list.Policies);
        Assert.True(await AllowsAsync(list, SeekerRouteTable.User(UserRoles.TaxiDriver)));
        await AssertOpenForAsync(
        [
            "POST /api/v1/trips/leads/{id:guid}/fare",
            "POST /api/v1/trips/leads/{id:guid}/not-available",
            "POST /api/v1/trips/leads/{id:guid}/confirm",
            "POST /api/v1/trips/leads/{id:guid}/deny",
        ], UserRoles.TaxiDriver);
    }

    // Both sides call these; the traveller side of a non-seeker is refused inside TripEnquiryAuth.RequireParty.
    [Fact]
    public Task Mixed_enquiry_routes_stay_open_at_the_route() => AssertOpenForAsync(
    [
        "GET /api/v1/trips/enquiries/{id:guid}",
        "GET /api/v1/trips/enquiries/by-conversation/{conversationId:guid}",
        "POST /api/v1/trips/enquiries/{id:guid}/cancel",
    ], UserRoles.StayOwner, UserRoles.TaxiDriver, UserRoles.Seeker);

    [Fact]
    public async Task Driver_taxi_request_routes_keep_the_driver_policy_and_ack_stays_anonymous()
    {
        var driver = Routes.Keys.Where(k => k.Contains(" /api/v1/taxi-requests/driver/", StringComparison.Ordinal)).ToList();
        Assert.Equal(12, driver.Count);
        foreach (var key in driver)
        {
            Assert.Equal(["CanListTaxis"], Route(key).Policies);
            Assert.True(await AllowsAsync(Route(key), SeekerRouteTable.User(UserRoles.TaxiDriver)), key);
        }
        var ack = Route("POST /api/v1/taxi-requests/offers/{offerId:guid}/ack");
        Assert.True(ack.AllowAnonymous);
        Assert.Empty(ack.Authorize);
    }

    [Fact]
    public Task Catalogue_reads_stay_open() => AssertOpenForAsync(
    [
        "GET /api/v1/trips/cities",
        "GET /api/v1/trips/cities/version",
        "GET /api/v1/trips/cities/nearest",
        "GET /api/v1/trips/map-packs/{stateId:guid}/{version}",
        "GET /api/v1/trips/cities/{cityKey:long}/spots/{spotId}",
        "GET /api/v1/taxis/ways",
        "GET /api/v1/taxis/ways/available",
        "GET /api/v1/taxis/ways/plans/{planKey}",
        "GET /api/v1/taxis/ways/legs/{legKey}/drivers",
        "GET /api/v1/taxis/route-feed",
    ], UserRoles.StayOwner, UserRoles.TaxiDriver, UserRoles.Seeker);

    [Fact]
    public Task Notifications_chat_plans_roles_users_owner_home_and_festivals_stay_open()
    {
        string[] prefixes = ["/api/v1/notifications", "/api/v1/chat/", "/api/v1/plans/", "/api/v1/roles", "/api/v1/users/", "/api/v1/owner/", "/api/v1/festivals"];
        var keys = OpenUser.Where(k => prefixes.Any(p => k[(k.IndexOf(' ') + 1)..].StartsWith(p, StringComparison.Ordinal))).ToList();
        Assert.Contains("GET /api/v1/notifications/notices", keys);
        Assert.Contains("GET /api/v1/owner/home", keys);
        Assert.Contains("GET /api/v1/festivals", keys);
        Assert.Contains("GET /api/v1/users/profile", keys);
        return AssertOpenForAsync(keys, UserRoles.StayOwner, UserRoles.TaxiDriver);
    }
}
