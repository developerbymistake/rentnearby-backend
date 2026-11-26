using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using static RentNearBy.Tests.SeekerOnly.SeekerRouteTable;

namespace RentNearBy.Tests.SeekerOnly;

// BE-T2 (SPEC R7): the SeekerOnly policy refuses every non-seeker principal, and every gated route group carries it, so an
// owner token is refused (403 SeekerOnly, see SeekerForbiddenResultHandlerTests) while a seeker token passes the gate.
public class SeekerPolicyTests
{
    public static TheoryData<string> NonSeekerRoles => new() { UserRoles.StayOwner, UserRoles.TaxiDriver, "RENT_OWNER", "" };

    [Theory]
    [MemberData(nameof(NonSeekerRoles))]
    public async Task Policy_refuses_an_owner_role(string role)
        => Assert.False((await Authorization.AuthorizeAsync(SeekerRouteTable.User(role), null, AuthPolicies.SeekerOnly)).Succeeded);

    [Fact]
    public async Task Policy_refuses_an_admin_token_and_a_token_without_a_role()
    {
        Assert.False((await Authorization.AuthorizeAsync(AdminUser(), null, AuthPolicies.SeekerOnly)).Succeeded);
        Assert.False((await Authorization.AuthorizeAsync(SeekerRouteTable.User(null), null, AuthPolicies.SeekerOnly)).Succeeded);
    }

    [Fact]
    public async Task Policy_passes_a_seeker()
        => Assert.True((await Authorization.AuthorizeAsync(SeekerRouteTable.User(UserRoles.Seeker), null, AuthPolicies.SeekerOnly)).Succeeded);

    [Fact]
    public async Task Policy_reads_only_the_role_claim_type_the_token_validation_writes()
    {
        var wrongType = SeekerRouteTable.User(null, new Claim(UserRoles.ClaimType, UserRoles.Seeker));
        Assert.False((await Authorization.AuthorizeAsync(wrongType, null, AuthPolicies.SeekerOnly)).Succeeded);
    }

    private static async Task AssertGatedAsync(string[] group)
    {
        Assert.NotEmpty(group);
        foreach (var key in group)
        {
            var route = Route(key);
            Assert.True(route.Policies.SequenceEqual([AuthPolicies.SeekerOnly]), key);
            Assert.False(await AllowsAsync(route, SeekerRouteTable.User(UserRoles.StayOwner)), $"{key}: a Stay Owner passed");
            Assert.False(await AllowsAsync(route, SeekerRouteTable.User(UserRoles.TaxiDriver)), $"{key}: a Taxi Driver passed");
            Assert.False(await AllowsAsync(route, AdminUser()), $"{key}: an admin token passed");
            Assert.True(await AllowsAsync(route, SeekerRouteTable.User(UserRoles.Seeker)), $"{key}: a seeker was refused");
        }
    }

    [Fact] public Task Trips_core_is_gated() => AssertGatedAsync(TripsCore);
    [Fact] public Task Planner_reads_are_gated() => AssertGatedAsync(PlannerReads);
    [Fact] public Task Execution_traveller_side_is_gated() => AssertGatedAsync(ExecutionTravellerSide);
    [Fact] public Task Shortlist_is_gated() => AssertGatedAsync(Shortlist);
    [Fact] public Task Trip_lifecycle_is_gated() => AssertGatedAsync(Lifecycle);
    [Fact] public Task Trip_sharing_is_gated() => AssertGatedAsync(Sharing);
    [Fact] public Task Trip_expenses_are_gated() => AssertGatedAsync(Expenses);
    [Fact] public Task Trip_payments_are_gated() => AssertGatedAsync(Payments);
    [Fact] public Task Taxi_request_t1_to_t3_are_gated() => AssertGatedAsync(TaxiRequestOnTrip);
    [Fact] public Task Taxi_request_t4_to_t10_are_gated() => AssertGatedAsync(TaxiRequest);
    [Fact] public Task Buddies_are_gated() => AssertGatedAsync(Buddies);
    [Fact] public Task Person_safety_and_report_reasons_are_gated() => AssertGatedAsync(PersonSafety);
    [Fact] public Task Saved_journeys_and_ways_ask_are_gated() => AssertGatedAsync(SeekerRouteTable.Journeys);
    [Fact] public Task Seeker_feed_is_gated() => AssertGatedAsync(SeekerFeed);

    [Fact]
    public void The_groups_cover_the_whole_gate()
        => Assert.Equal(Seeker.Order(), Routes.Values.Where(r => r.Policies.Contains(AuthPolicies.SeekerOnly)).Select(r => r.Key).Order());
}
