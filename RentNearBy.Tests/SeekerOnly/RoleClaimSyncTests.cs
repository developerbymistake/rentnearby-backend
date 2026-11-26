using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using RentNearBy.Api.Extensions;
using RentNearBy.Infrastructure.Repositories;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Integration.Journeys;
using static RentNearBy.Tests.SeekerOnly.SeekerRouteTable;

namespace RentNearBy.Tests.SeekerOnly;

// BE-T5 (contract 2.2): the role claim follows the live role of the session's user, so a token issued before a role
// choice cannot keep the seeker gate open for an owner account.
public class RoleClaimSyncTests
{
    [Fact]
    public async Task Stale_seeker_claim_of_a_stay_owner_loses_the_seeker_gate()
    {
        var user = SeekerRouteTable.User(UserRoles.Seeker);
        Assert.True((await Authorization.AuthorizeAsync(user, null, AuthPolicies.SeekerOnly)).Succeeded);

        RoleClaimSync.Apply(user, UserRoles.StayOwnerId);

        Assert.False((await Authorization.AuthorizeAsync(user, null, AuthPolicies.SeekerOnly)).Succeeded);
        Assert.True((await Authorization.AuthorizeAsync(user, null, "CanListStays")).Succeeded);
        Assert.Equal([UserRoles.StayOwner], user.FindAll(ClaimTypes.Role).Select(c => c.Value));
        Assert.False(SeekerGate.IsSeeker(user));
    }

    [Fact]
    public void Same_role_leaves_the_identity_untouched()
    {
        var user = SeekerRouteTable.User(UserRoles.TaxiDriver);
        var before = user.FindFirst(ClaimTypes.Role);
        var count = user.Claims.Count();

        RoleClaimSync.Apply(user, UserRoles.TaxiDriverId);

        Assert.Same(before, user.FindFirst(ClaimTypes.Role));
        Assert.Equal(count, user.Claims.Count());
    }

    [Fact]
    public void Both_claim_types_are_replaced_by_one_live_role_claim()
    {
        var user = SeekerRouteTable.User(UserRoles.StayOwner, new Claim(UserRoles.ClaimType, UserRoles.StayOwner), new Claim(ClaimTypes.Role, UserRoles.TaxiDriver));

        RoleClaimSync.Apply(user, UserRoles.SeekerId);

        Assert.Equal([UserRoles.Seeker], user.FindAll(ClaimTypes.Role).Select(c => c.Value));
        Assert.Empty(user.FindAll(UserRoles.ClaimType));
        Assert.True(SeekerGate.IsSeeker(user));
    }

    [Fact]
    public void A_token_without_a_role_claim_gets_the_live_role()
    {
        var user = SeekerRouteTable.User(null);
        RoleClaimSync.Apply(user, UserRoles.SeekerId);
        Assert.True(SeekerGate.IsSeeker(user));
    }

    [Fact]
    public void Unknown_role_id_throws_and_changes_nothing()
    {
        var user = SeekerRouteTable.User(UserRoles.Seeker);
        Assert.Throws<InvalidOperationException>(() => RoleClaimSync.Apply(user, Guid.NewGuid()));
        Assert.True(SeekerGate.IsSeeker(user));
    }
}

// Needs RENTNEARBY_TEST_DB (PostGIS); skipped without it. Written, not run on the build PC.
public class SessionRoleIntegrationTests(TestDatabase db) : IntegrationTestBase(db)
{
    [IntegrationFact]
    public async Task Active_session_answers_the_current_role_and_an_ended_one_answers_null()
    {
        await using var h = new JourneyHarness(Db);
        var userId = await h.AddStayOwnerAsync();
        var live = Guid.NewGuid();
        var revoked = Guid.NewGuid();
        var expired = Guid.NewGuid();
        await using (var ctx = CreateContext())
        {
            var now = DateTime.UtcNow;
            ctx.Sessions.AddRange(
                new Session { Id = live, UserId = userId, CreatedAt = now, ExpiresAt = now.AddDays(1), IsActive = true },
                new Session { Id = revoked, UserId = userId, CreatedAt = now, ExpiresAt = now.AddDays(1), IsActive = false },
                new Session { Id = expired, UserId = userId, CreatedAt = now.AddDays(-2), ExpiresAt = now.AddDays(-1), IsActive = true });
            await ctx.SaveChangesAsync();
        }

        await using var read = CreateContext();
        var sessions = new SessionRepository(read);
        Assert.Equal(UserRoles.StayOwnerId, await sessions.GetActiveSessionRoleAsync(live));
        Assert.Null(await sessions.GetActiveSessionRoleAsync(revoked));
        Assert.Null(await sessions.GetActiveSessionRoleAsync(expired));
        Assert.Null(await sessions.GetActiveSessionRoleAsync(Guid.NewGuid()));

        await read.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s.SetProperty(u => u.RoleId, UserRoles.SeekerId));
        Assert.Equal(UserRoles.SeekerId, await sessions.GetActiveSessionRoleAsync(live));
    }
}
