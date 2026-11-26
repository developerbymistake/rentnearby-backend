using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RentNearBy.Core.DTOs.Buddy;
using RentNearBy.Core.Entities.Buddy;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.Buddy;
using RentNearBy.Infrastructure.Services.Buddy.Be2;
using RentNearBy.Infrastructure.Services.Buddy.Be3;
using RentNearBy.Infrastructure.Services.Buddy.Be4;
using RentNearBy.Tests.Buddy.Be1;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Support.Buddy;

namespace RentNearBy.Tests.SeekerOnly;

// PLAN_BE 6.5 sets 1-6 (contract BE-T6). Needs RENTNEARBY_TEST_DB (PostGIS); skipped without it. Written, not run on the
// build PC. Calls the same service methods the handlers call; a role change is a plain RoleId update (nothing else moves).
public class SeekerOnlyIntegrationTests(TestDatabase db) : IntegrationTestBase(db)
{
    private async Task<List<Guid>> PeopleAsync(int n)
    {
        await using var ctx = CreateContext();
        return await BuddySeed.UsersAsync(ctx, n);
    }

    private async Task SetRoleAsync(Guid userId, Guid roleId)
    {
        await using var ctx = CreateContext();
        await ctx.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s.SetProperty(u => u.RoleId, roleId));
    }

    private async Task<string> UsernameAsync(Guid id)
    {
        await using var ctx = CreateContext();
        return (await ctx.Users.AsNoTracking().Where(u => u.Id == id).Select(u => u.Username).SingleAsync())!;
    }

    private static TripMoneyCards Cards(ApplicationDbContext ctx) => new(ctx, new TripLedger(ctx, NullLogger<TripLedger>.Instance));

    private static TripInviteService Invites(ApplicationDbContext ctx) =>
        new(ctx, new NoticeWriter(ctx), new RecordingLiveHints(), TimeProvider.System, NullLogger<TripInviteService>.Instance);

    private static TripSharedListReader Shared(ApplicationDbContext ctx) => new(ctx, Cards(ctx), TimeProvider.System);

    // Set 1: find by username of an owner-role account answers byte for byte like an unknown username.
    [IntegrationFact]
    public async Task Find_of_a_non_seeker_is_the_unknown_answer()
    {
        var p = await PeopleAsync(2);
        await SetRoleAsync(p[1], UserRoles.StayOwnerId);
        await using var ctx = CreateContext();
        var find = new Be1Kit(ctx).Find;
        var owner = await find.FindAsync(p[0], await UsernameAsync(p[1]));
        var unknown = await find.FindAsync(p[0], "Bakhli#99999998");
        Assert.False(owner.Found);
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Assert.Equal(JsonSerializer.Serialize(unknown, web), JsonSerializer.Serialize(owner, web));
    }

    // Set 2: a request to a Taxi Driver id is PersonNotFound and writes nothing.
    [IntegrationFact]
    public async Task Send_to_a_non_seeker_is_person_not_found_and_writes_nothing()
    {
        var p = await PeopleAsync(2);
        await SetRoleAsync(p[1], UserRoles.TaxiDriverId);
        await using var ctx = CreateContext();
        var kit = new Be1Kit(ctx);
        var r = await kit.Requests.SendAsync(p[0], p[1], BuddyRequestSources.Search, Guid.NewGuid());
        Assert.Equal(BuddyErrorTypes.PersonNotFound, r.ErrorType);
        Assert.Empty(kit.Notices.Upserts);
        Assert.Empty(kit.Notices.Pushes);
        Assert.Equal(0, await ctx.Set<BuddyRequest>().CountAsync(x => x.FromUserId == p[0]));
    }

    // Set 3: a pending request is hidden from both sides while one side is not a seeker, cannot be accepted, and is not deleted.
    [IntegrationFact]
    public async Task Pending_request_with_a_non_seeker_is_hidden_and_cannot_be_accepted()
    {
        var p = await PeopleAsync(2);
        Guid requestId;
        await using (var seed = CreateContext())
            requestId = (await BuddySeed.RequestsAsync(seed, p[0], [p[1]]))[0];

        await SetRoleAsync(p[1], UserRoles.StayOwnerId);
        await using (var ctx = CreateContext())
            Assert.Empty((await new Be1Kit(ctx).Lists.SentAsync(p[0], null, 20)).Items);

        await SetRoleAsync(p[1], UserRoles.SeekerId);
        await SetRoleAsync(p[0], UserRoles.StayOwnerId);
        await using (var ctx = CreateContext())
        {
            var kit = new Be1Kit(ctx);
            Assert.Empty((await kit.Lists.ReceivedAsync(p[1], null, 20)).Items);
            Assert.Equal(0, (await kit.Lists.SummaryAsync(p[1])).ReceivedPending);
            Assert.Equal(BuddyErrorTypes.RequestEnded, (await kit.Requests.AcceptAsync(p[1], requestId)).ErrorType);
            Assert.Equal(0, await ctx.Set<BuddyLink>().CountAsync(x => x.UserId == p[1]));
            Assert.Equal(BuddyRequestStatuses.Pending, (await ctx.Set<BuddyRequest>().AsNoTracking().SingleAsync(x => x.Id == requestId)).Status);
        }

        await SetRoleAsync(p[0], UserRoles.SeekerId);
        await using (var ctx = CreateContext())
            Assert.Single((await new Be1Kit(ctx).Lists.ReceivedAsync(p[1], null, 20)).Items);
    }

    // Set 4: a buddy who is no longer a seeker is NotBuddy for a trip invite; no member row is written.
    [IntegrationFact]
    public async Task Invite_of_a_non_seeker_buddy_is_not_buddy()
    {
        var p = await PeopleAsync(2);
        Guid trip;
        await using (var seed = CreateContext())
        {
            await BuddySeed.BuddiesAsync(seed, p[0], [p[1]]);
            trip = await BuddySeed.TripAsync(seed, p[0]);
        }
        await SetRoleAsync(p[1], UserRoles.TaxiDriverId);

        await using var ctx = CreateContext();
        var r = await Invites(ctx).InviteAsync(trip, p[0], [p[1]]);
        Assert.True(r.Ok);
        Assert.Equal(InviteOutcomes.NotBuddy, Assert.Single(r.Value!.Results).Outcome);
        Assert.Equal(0, await ctx.Set<TripMember>().CountAsync(m => m.TripId == trip));
    }

    // Set 5: a waiting invite of a trip owner who is no longer a seeker has ended for the invitee and is not listed.
    [IntegrationFact]
    public async Task Invite_of_a_non_seeker_trip_owner_has_ended()
    {
        var p = await PeopleAsync(2);
        Guid member;
        await using (var seed = CreateContext())
        {
            var trip = await BuddySeed.TripAsync(seed, p[0]);
            member = (await BuddySeed.MembersAsync(seed, trip, p[0], [p[1]], TripMemberStatuses.Invited))[0];
        }
        await SetRoleAsync(p[0], UserRoles.StayOwnerId);

        await using (var ctx = CreateContext())
        {
            var preview = await Invites(ctx).PreviewAsync(member, p[1]);
            Assert.Equal(BuddyErrorTypes.InviteEnded, preview.ErrorType);
            var data = Assert.IsType<InviteEndedDataDto>(preview.ErrorData);
            Assert.Equal(InviteEndedReasons.Unknown, data.Reason);
            Assert.Null(data.Title);
            Assert.Null(data.OwnerName);
            Assert.Equal(BuddyErrorTypes.InviteEnded, (await Invites(ctx).AcceptAsync(member, p[1])).ErrorType);
            Assert.Empty((await Shared(ctx).GetAsync(p[1], SharedListStates.Upcoming, null, 20)).Invites);
            Assert.Equal(TripMemberStatuses.Invited, (await ctx.Set<TripMember>().AsNoTracking().SingleAsync(m => m.Id == member)).Status);
        }

        await SetRoleAsync(p[0], UserRoles.SeekerId);
        await using (var ctx = CreateContext())
            Assert.Single((await Shared(ctx).GetAsync(p[1], SharedListStates.Upcoming, null, 20)).Invites);
    }

    // Set 6 (R4): a role change hides the traveller notices and the buddy, deletes nothing, and a seeker again sees it all.
    [IntegrationFact]
    public async Task Role_change_round_trip_hides_and_brings_back()
    {
        var p = await PeopleAsync(2);
        await using (var seed = CreateContext())
        {
            await BuddySeed.BuddiesAsync(seed, p[0], [p[1]]);
            var trip = await BuddySeed.TripAsync(seed, p[0]);
            await BuddySeed.NoticesAsync(seed, p[0], [(NoticeKinds.BuddyAccepted, NoticeKinds.NoTrip), (NoticeKinds.PlanLimitsChanged, NoticeKinds.NoTrip)]);
            await BuddySeed.NoticesAsync(seed, p[0], [(NoticeKinds.TripJoined, trip)]);
        }

        await SetRoleAsync(p[0], UserRoles.StayOwnerId);
        await using (var ctx = CreateContext())
        {
            var reader = new NoticeReader(ctx);
            var page = await reader.PageAsync(p[0], seeker: false, null, 20);
            Assert.Equal([NoticeKinds.PlanLimitsChanged], page.Items.Select(i => i.Kind));
            Assert.Equal(1, await reader.UnreadCountAsync(p[0], seeker: false));
            await reader.MarkAllReadAsync(p[0], seeker: false);
            Assert.Equal(3, await ctx.Set<UserNotice>().CountAsync(n => n.UserId == p[0]));
            Assert.Equal(2, await ctx.Set<UserNotice>().CountAsync(n => n.UserId == p[0] && n.ReadAt == null));
            Assert.Empty((await new Be1Kit(ctx).Lists.BuddiesAsync(p[1], null, 50, null))!.Items);
            Assert.Equal(2, await ctx.Set<BuddyLink>().CountAsync(x => x.UserId == p[0] || x.UserId == p[1]));
        }

        await SetRoleAsync(p[0], UserRoles.SeekerId);
        await using (var ctx = CreateContext())
        {
            var reader = new NoticeReader(ctx);
            Assert.Equal(3, (await reader.PageAsync(p[0], seeker: true, null, 20)).Items.Count);
            Assert.Equal(2, await reader.UnreadCountAsync(p[0], seeker: true));
            var buddies = (await new Be1Kit(ctx).Lists.BuddiesAsync(p[1], null, 50, null))!;
            Assert.Equal(p[0], Assert.Single(buddies.Items).UserId);
            Assert.Equal(1, buddies.Count);
        }
    }
}
