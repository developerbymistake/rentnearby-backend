using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RentNearBy.Core.Entities.Social;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.Buddy;
using RentNearBy.Infrastructure.Services.Buddy.Be2;
using RentNearBy.Infrastructure.Services.Buddy.Be3;
using RentNearBy.Infrastructure.Services.Buddy.Be4;
using RentNearBy.Infrastructure.Services.Social;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Support.Buddy;
using RentNearBy.Tests.Support.Social;

namespace RentNearBy.Tests.Social;

// Contract 3.5: the trip room exists from the first join of any kind, a join line per joiner, a later joiner starts at the
// current seq, a leaver loses the room. Needs RENTNEARBY_TEST_DB; skipped without it.
public class TripGroupChatIntegrationTests(TestDatabase db) : IntegrationTestBase(db)
{
    private static TripInviteService Invites(ApplicationDbContext ctx)
    {
        var ac = SocialTestSeams.AfterCommit(ctx);
        return new(ctx, new NoticeWriter(ctx), new RecordingLiveHints(), TimeProvider.System, NullLogger<TripInviteService>.Instance,
            SocialTestSeams.Limits(10), SocialTestSeams.GroupChat(ctx, ac), ac);
    }

    private static TripMemberService Members(ApplicationDbContext ctx)
    {
        var ac = SocialTestSeams.AfterCommit(ctx);
        return new(ctx, new TripAccess(ctx), new TripMoneyCards(ctx, new TripLedger(ctx, NullLogger<TripLedger>.Instance)), new NoticeWriter(ctx),
            new RecordingLiveHints(), TimeProvider.System, NullLogger<TripMemberService>.Instance, SocialTestSeams.GroupChat(ctx, ac), ac);
    }

    private async Task<(Guid Owner, Guid A, Guid B, Guid Trip, List<Guid> Invites)> InvitedTripAsync()
    {
        await using var ctx = CreateContext();
        var p = await BuddySeed.UsersAsync(ctx, 3);
        var trip = await BuddySeed.TripAsync(ctx, p[0]);
        var invites = await BuddySeed.MembersAsync(ctx, trip, p[0], [p[1], p[2]], TripMemberStatuses.Invited);
        return (p[0], p[1], p[2], trip, invites);
    }

    private async Task<ChatRoom?> RoomAsync(Guid trip)
    {
        await using var ctx = CreateContext();
        return await ctx.Set<ChatRoom>().AsNoTracking().SingleOrDefaultAsync(r => r.TripId == trip && r.Kind == ChatKinds.Trip);
    }

    [IntegrationFact]
    public async Task Invite_accept_opens_the_room_with_a_join_line_and_a_later_joiner_starts_at_the_current_seq()
    {
        var (owner, a, b, trip, invites) = await InvitedTripAsync();
        Assert.Null(await RoomAsync(trip));

        await using (var ctx = CreateContext())
            Assert.True((await Invites(ctx).AcceptAsync(invites[0], a)).Ok);
        var room = await RoomAsync(trip);
        Assert.NotNull(room);
        Assert.Equal(1, room!.LastSeq);

        await using (var ctx = CreateContext())
            Assert.True((await Invites(ctx).AcceptAsync(invites[1], b)).Ok);

        await using var check = CreateContext();
        var members = await check.Set<ChatRoomMember>().AsNoTracking().Where(m => m.RoomId == room.Id).ToListAsync();
        Assert.Equal(new[] { owner, a, b }.OrderBy(x => x), members.Select(m => m.UserId).OrderBy(x => x));
        Assert.Equal(0, members.Single(m => m.UserId == a).JoinedSeq);
        Assert.Equal(1, members.Single(m => m.UserId == b).JoinedSeq);
        var lines = await check.Set<ChatMessage>().AsNoTracking().Where(m => m.RoomId == room.Id).OrderBy(m => m.Seq).ToListAsync();
        Assert.Equal(new[] { a, b }, lines.Select(l => l.ActorUserId!.Value));
        Assert.All(lines, l => Assert.Equal(ChatSystemKinds.Joined, l.SystemKind));
        Assert.All(lines, l => Assert.Null(l.SenderId));
    }

    [IntegrationFact]
    public async Task A_leaver_loses_the_room_and_a_left_line_is_written()
    {
        var (_, a, _, trip, invites) = await InvitedTripAsync();
        await using (var ctx = CreateContext())
            Assert.True((await Invites(ctx).AcceptAsync(invites[0], a)).Ok);
        await using (var ctx = CreateContext())
            Assert.True((await Members(ctx).LeaveAsync(trip, a)).Ok);

        var room = (await RoomAsync(trip))!;
        await using var check = CreateContext();
        var row = await check.Set<ChatRoomMember>().AsNoTracking().SingleAsync(m => m.RoomId == room.Id && m.UserId == a);
        Assert.NotNull(row.LeftAt);
        Assert.Equal(ChatBuckets.Hidden, row.ListBucket);
        var last = await check.Set<ChatMessage>().AsNoTracking().Where(m => m.RoomId == room.Id).OrderByDescending(m => m.Seq).FirstAsync();
        Assert.Equal((ChatSystemKinds.Left, (Guid?)a), (last.SystemKind, last.ActorUserId));
    }
}
