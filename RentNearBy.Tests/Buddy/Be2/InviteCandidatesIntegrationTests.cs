using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Entities.Social;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.Buddy.Be1;
using RentNearBy.Infrastructure.Services.Buddy.Be2;
using RentNearBy.Infrastructure.Services.Buddy.Be4;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Support.Buddy;
using RentNearBy.Tests.Support.Social;

namespace RentNearBy.Tests.Buddy.Be2;

// Owner 2026-11-28 (I-13): the trip invite lists the people I follow (buddies first, starred) and an invite reaches anyone I
// follow; a person I do not follow stays NotBuddy. Needs RENTNEARBY_TEST_DB (PostGIS); skipped without it.
public class InviteCandidatesIntegrationTests(TestDatabase db) : IntegrationTestBase(db)
{
    private static BuddyListService Lists(ApplicationDbContext ctx) => new(ctx, new RecordingLiveHints(), NullLogger<BuddyListService>.Instance);

    private static TripInviteService Invites(ApplicationDbContext ctx)
    {
        var ac = SocialTestSeams.AfterCommit(ctx);
        return new TripInviteService(ctx, new NoticeWriter(ctx), new RecordingLiveHints(), TimeProvider.System, NullLogger<TripInviteService>.Instance,
            SocialTestSeams.Limits(TripLimits.MaxTripsPerUser), SocialTestSeams.GroupChat(ctx, ac), ac);
    }

    private static void AddFollow(ApplicationDbContext ctx, Guid me, Guid him, string status = FollowStatuses.Accepted)
        => ctx.Set<Follow>().Add(new Follow { FollowerId = me, FolloweeId = him, Status = status, CreatedAt = DateTime.UtcNow });

    [IntegrationFact]
    public async Task Candidates_are_the_people_i_follow_buddies_first_with_trip_state()
    {
        await using var ctx = CreateContext();
        var p = await BuddySeed.UsersAsync(ctx, 8);
        var me = p[0];
        var trip = await BuddySeed.TripAsync(ctx, me);
        foreach (var him in p.Skip(1).Take(5)) AddFollow(ctx, me, him); // batch-loop: test seed
        AddFollow(ctx, me, p[6], FollowStatuses.Requested);                // not accepted yet: left out
        AddFollow(ctx, p[7], me);                                           // follows me, I do not follow him: left out
        ctx.UserBlocks.Add(new UserBlock { Id = Guid.NewGuid(), BlockerId = p[5], BlockedId = me, CreatedAt = DateTime.UtcNow });
        await ctx.SaveChangesAsync();
        await BuddySeed.BuddiesAsync(ctx, me, [p[4]]);
        await BuddySeed.MembersAsync(ctx, trip, me, [p[2]]);
        await ctx.Users.Where(u => u.Id == p[3]).ExecuteUpdateAsync(s => s.SetProperty(u => u.RoleId, UserRoles.TaxiDriverId));

        var page = (await Lists(ctx).InviteCandidatesAsync(me, trip, null, 20))!;
        Assert.Equal([p[4], p[1], p[2]], page.Items.Select(i => i.UserId).ToList());   // buddy first, then by name
        Assert.Equal(3, page.Count);
        Assert.Equal([true, false, false], page.Items.Select(i => i.IsBuddy).ToList());
        Assert.Equal(BuddyRules.TripState(TripMemberStatuses.Joined, null, null, DateTime.UtcNow), page.Items[2].TripState);
        Assert.Null(page.NextCursor);
    }

    [IntegrationFact]
    public async Task Paging_crosses_the_buddy_boundary_without_skips_or_repeats()
    {
        await using var ctx = CreateContext();
        var p = await BuddySeed.UsersAsync(ctx, 6);
        var me = p[0];
        var trip = await BuddySeed.TripAsync(ctx, me);
        foreach (var him in p.Skip(1)) AddFollow(ctx, me, him); // batch-loop: test seed
        await ctx.SaveChangesAsync();
        await BuddySeed.BuddiesAsync(ctx, me, [p[3], p[5]]);

        var seen = new List<Guid>();
        (string LowerName, Guid Id)? cursor = null;
        for (var i = 0; i < 5; i++) // batch-loop: pages of 2
        {
            var page = (await Lists(ctx).InviteCandidatesAsync(me, trip, cursor, 2))!;
            seen.AddRange(page.Items.Select(x => x.UserId));
            if (page.NextCursor is null) break;
            Assert.True(BuddyCursors.TryDecodeName(page.NextCursor, out var name, out var id));
            cursor = (name, id);
        }
        Assert.Equal([p[3], p[5], p[1], p[2], p[4]], seen);
    }

    [IntegrationFact]
    public async Task Only_the_trip_owner_reads_the_candidates()
    {
        await using var ctx = CreateContext();
        var p = await BuddySeed.UsersAsync(ctx, 2);
        var trip = await BuddySeed.TripAsync(ctx, p[0]);
        Assert.Null(await Lists(ctx).InviteCandidatesAsync(p[1], trip, null, 20));
        Assert.Null(await Lists(ctx).InviteCandidatesAsync(p[0], Guid.NewGuid(), null, 20));
        Assert.False(BuddyListService.TrySplitCandidateCursor(("x", Guid.NewGuid()), out _, out _));
    }

    [IntegrationFact]
    public async Task Invite_reaches_anyone_i_follow_and_not_a_person_i_do_not_follow()
    {
        await using var ctx = CreateContext();
        var p = await BuddySeed.UsersAsync(ctx, 5);
        var me = p[0];
        var trip = await BuddySeed.TripAsync(ctx, me);
        AddFollow(ctx, me, p[1]);                                   // followed, not a buddy
        AddFollow(ctx, p[2], me);                                   // only follows me
        AddFollow(ctx, me, p[3], FollowStatuses.Requested);         // request not accepted
        await ctx.SaveChangesAsync();
        await BuddySeed.BuddiesAsync(ctx, me, [p[4]]);           // a buddy stays invitable

        var result = await Invites(ctx).InviteAsync(trip, me, [p[1], p[2], p[3], p[4]]);
        Assert.True(result.Ok, result.ErrorType);
        var outcomes = result.Value!.Results.ToDictionary(r => r.UserId, r => r.Outcome);
        Assert.Equal(InviteOutcomes.Invited, outcomes[p[1]]);
        Assert.Equal(InviteOutcomes.NotBuddy, outcomes[p[2]]);
        Assert.Equal(InviteOutcomes.NotBuddy, outcomes[p[3]]);
        Assert.Equal(InviteOutcomes.Invited, outcomes[p[4]]);
        Assert.Equal(2, await ctx.Set<RentNearBy.Core.Entities.Buddy.TripMember>().CountAsync(m => m.TripId == trip));
    }

    [IntegrationFact]
    public async Task A_blocked_person_i_follow_is_not_invited()
    {
        await using var ctx = CreateContext();
        var p = await BuddySeed.UsersAsync(ctx, 2);
        var trip = await BuddySeed.TripAsync(ctx, p[0]);
        AddFollow(ctx, p[0], p[1]);
        ctx.UserBlocks.Add(new UserBlock { Id = Guid.NewGuid(), BlockerId = p[1], BlockedId = p[0], CreatedAt = DateTime.UtcNow });
        await ctx.SaveChangesAsync();
        var result = await Invites(ctx).InviteAsync(trip, p[0], [p[1]]);
        Assert.Equal(InviteOutcomes.NotBuddy, Assert.Single(result.Value!.Results).Outcome);
    }
}
