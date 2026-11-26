using static RentNearBy.Tests.SeekerOnly.SeekerRouteTable;

namespace RentNearBy.Tests.Social;

// Contract 2: every /api/v1/social route is SeekerOnly, except onboarding (any logged-in role); the admin routes are AdminOnly.
public class SocialRouteGuardTests
{
    private const string Onboarding = "PUT /api/v1/social/me/onboarding";

    private static readonly string[] AdminRoutes =
    [
        "GET /api/v1/admin/reports/{id:guid}/snapshot",
        "POST /api/v1/admin/social/trips/{tripId:guid}/unpublish",
        "PUT /api/v1/admin/users/{id:guid}/identity",
    ];

    [Fact]
    public void Social_routes_are_seeker_only_except_onboarding()
    {
        var social = Routes.Where(r => r.Key.Contains(" /api/v1/social/", StringComparison.Ordinal) || r.Key.EndsWith(" /api/v1/social", StringComparison.Ordinal)).ToList();
        Assert.Equal(35, social.Count);
        foreach (var (key, route) in social.Where(r => r.Key != Onboarding))
            Assert.True(route.Policies.SequenceEqual([AuthPolicies.SeekerOnly]), key);
        var onboarding = Route(Onboarding);
        Assert.NotEmpty(onboarding.Authorize);
        Assert.DoesNotContain(AuthPolicies.SeekerOnly, onboarding.Policies);
        Assert.False(onboarding.AllowAnonymous);
    }

    [Fact]
    public void Social_list_holds_34_seeker_routes_and_the_gate_total_is_123()
    {
        Assert.Equal(34, SeekerOnly.SeekerRouteTable.Social.Length);
        Assert.Equal(123, Seeker.Length);
        Assert.DoesNotContain(Onboarding, SeekerOnly.SeekerRouteTable.Social);
        Assert.Contains(Onboarding, OpenUser);
    }

    [Fact]
    public void Social_admin_routes_are_admin_only()
    {
        foreach (var key in AdminRoutes)
            Assert.Contains("AdminOnly", Route(key).Policies);
    }

    [Fact]
    public void Retired_buddy_request_routes_are_not_mapped()
    {
        foreach (var key in new[]
                 {
                     "POST /api/v1/buddies/requests", "GET /api/v1/buddies/requests", "POST /api/v1/buddies/requests/{id:guid}/accept",
                     "POST /api/v1/buddies/requests/{id:guid}/decline", "POST /api/v1/buddies/requests/{id:guid}/cancel", "POST /api/v1/buddies/seen",
                 })
            Assert.False(Routes.ContainsKey(key), key);
        Assert.True(Routes.ContainsKey("DELETE /api/v1/buddies/{userId:guid}"));
        Assert.True(Routes.ContainsKey("GET /api/v1/buddies"));
    }
}
