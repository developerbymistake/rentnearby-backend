using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using RentNearBy.Api.Handlers;
using RentNearBy.Api.Validators;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Integration.Journeys;
using static RentNearBy.Tests.Integration.Journeys.JourneyHarness;

namespace RentNearBy.Tests.Roles;

// Social next item 2 (owner 2026-11-27): POST /users/me/role-request refuses a Traveller (Seeker) with a typed 403; Stay Owners
// and Taxi Drivers keep the request exactly as before. DB-backed, skipped without PostgreSQL.
public class RoleRequestSeekerIntegrationTests(TestDatabase db) : IntegrationTestBase(db)
{
    private static string? ErrorTypeOf(IResult r) => (((IValueHttpResult)r).Value as ApiResponse<object>)?.Error?.Type;

    private sealed class NoBlockingListings : IRoleChangeGuardService
    {
        public Task<int> GetBlockingCountAsync(Guid userId, string leavingKind) => Task.FromResult(0);

        public Task<IReadOnlyDictionary<Guid, BlockingListingCounts>> GetBlockingCountsAsync(IReadOnlyCollection<Guid> userIds)
            => Task.FromResult<IReadOnlyDictionary<Guid, BlockingListingCounts>>(new Dictionary<Guid, BlockingListingCounts>());
    }

    private static async Task<Guid> ConfirmedAsync(JourneyHarness h, Guid roleId)
    {
        var id = await h.AddUserAsync(roleId, "Role Tester");
        await using var s = h.NewScope();
        await s.Db.Users.Where(u => u.Id == id).ExecuteUpdateAsync(x => x.SetProperty(u => u.RoleConfirmedAt, DateTime.UtcNow.AddDays(-20)));
        return id;
    }

    private static async Task<(IResult Result, RecordingPublisher Publisher)> RequestAsync(JourneyHarness h, Guid userId, string toRole)
    {
        await using var s = h.NewScope();
        var publisher = new RecordingPublisher();
        var result = await RoleHandlers.CreateRoleRequest(new RoleKeyRequest(toRole), new RoleKeyRequestValidator(), Principal(userId),
            s.Uow, publisher, new NoBlockingListings(), h.RateLimit, s.Http, h.Cache);
        return (result, publisher);
    }

    private static async Task<int> RequestRowsAsync(JourneyHarness h, Guid userId)
    {
        await using var s = h.NewScope();
        return await s.Db.RoleChangeRequests.AsNoTracking().CountAsync(r => r.UserId == userId);
    }

    [IntegrationTheory]
    [InlineData(UserRoles.StayOwner)]
    [InlineData(UserRoles.TaxiDriver)]
    public async Task Seeker_role_request_is_refused_with_a_typed_error(string toRole)
    {
        await using var h = new JourneyHarness(Db);
        var seeker = await ConfirmedAsync(h, UserRoles.SeekerId);

        var (result, publisher) = await RequestAsync(h, seeker, toRole);

        Assert.Equal(403, StatusOf(result));
        Assert.Equal(RoleChangeRules.NotAllowedErrorType, ErrorTypeOf(result));
        Assert.Equal(0, await RequestRowsAsync(h, seeker));
        Assert.Empty(publisher.Published);
    }

    [IntegrationFact]
    public async Task Stay_owner_request_is_unchanged()
    {
        await using var h = new JourneyHarness(Db);
        var owner = await ConfirmedAsync(h, UserRoles.StayOwnerId);

        var (result, publisher) = await RequestAsync(h, owner, UserRoles.TaxiDriver);

        Assert.Equal(201, StatusOf(result));
        Assert.Equal(1, await RequestRowsAsync(h, owner));
        Assert.Single(publisher.Published);
    }

    [IntegrationFact]
    public async Task Taxi_driver_may_still_ask_to_become_a_traveller()
    {
        await using var h = new JourneyHarness(Db);
        var driver = await ConfirmedAsync(h, UserRoles.TaxiDriverId);

        var (result, _) = await RequestAsync(h, driver, UserRoles.Seeker);

        Assert.Equal(201, StatusOf(result));
        Assert.Equal(1, await RequestRowsAsync(h, driver));
    }

    // The refusal comes after 'Select your role first': an unconfirmed account still goes through the first role choice.
    [IntegrationFact]
    public async Task Unconfirmed_seeker_is_still_told_to_pick_a_role_first()
    {
        await using var h = new JourneyHarness(Db);
        var fresh = await h.AddSeekerAsync();

        var (result, _) = await RequestAsync(h, fresh, UserRoles.StayOwner);

        Assert.Equal(400, StatusOf(result));
        Assert.Equal("RoleNotConfirmed", ErrorTypeOf(result));
    }
}
