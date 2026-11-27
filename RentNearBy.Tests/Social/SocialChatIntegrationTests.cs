using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RentNearBy.Core.Entities.Social;
using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.Buddy.Be4;
using RentNearBy.Infrastructure.Services.Social;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Support.Buddy;
using RentNearBy.Tests.Support.Social;

namespace RentNearBy.Tests.Social;

// Contract 2.6 / 3.4. Needs RENTNEARBY_TEST_DB; skipped without it.
public class SocialChatIntegrationTests(TestDatabase db) : IntegrationTestBase(db)
{
    private static SocialChatService Chat(ApplicationDbContext ctx, bool plus = false)
        => new(ctx, SocialTestSeams.Limits(10, plus), new NoticeWriter(ctx), SocialTestSeams.AfterCommit(ctx), TimeProvider.System,
            NullLogger<SocialChatService>.Instance);

    private async Task<List<Guid>> PeopleAsync(int n)
    {
        await using var ctx = CreateContext();
        var ids = await BuddySeed.UsersAsync(ctx, n);
        foreach (var id in ids) await SocialSeed.ProfileAsync(ctx, id);
        return ids;
    }

    [IntegrationFact]
    // I-9 (owner 2026-11-28): a Free user sends ONE message in each chat, then the Plus lock; a reply never unlocks it.
    public async Task Free_sends_one_message_per_chat_and_a_reply_never_unlocks()
    {
        var p = await PeopleAsync(2);
        await using var ctx = CreateContext();
        var pre = (await Chat(ctx).WithUserAsync(p[0], p[1])).Value!;
        Assert.Equal(ChatLocks.None, pre.Lock);                       // never locked before the first message
        var started = await Chat(ctx).StartAsync(p[0], p[1], Guid.NewGuid(), "Hi, going to Manali too?");
        Assert.True(started.Ok, started.ErrorType);
        var room = started.Value!.Room.RoomId;
        Assert.Equal((ChatRoomStatuses.Request, ChatLocks.Plus), (started.Value.Room.Status, started.Value.Room.Lock));
        Assert.Equal(SocialErrorTypes.PlusRequired, (await Chat(ctx).SendAsync(p[0], room, Guid.NewGuid(), "hello?")).ErrorType);
        Assert.Equal(ChatLocks.None, (await Chat(ctx).RoomAsync(p[1], room))!.Lock);   // the receiver may still send his one

        var reply = await Chat(ctx).SendAsync(p[1], room, Guid.NewGuid(), "Yes!");
        Assert.True(reply.Ok, reply.ErrorType);
        Assert.Equal(2, reply.Value!.Seq);
        var starterView = (await Chat(ctx).RoomAsync(p[0], room))!;
        Assert.Equal((ChatRoomStatuses.Active, ChatLocks.Plus, false), (starterView.Status, starterView.Lock, starterView.CanSend));
        Assert.Equal(SocialErrorTypes.PlusRequired, (await Chat(ctx).SendAsync(p[0], room, Guid.NewGuid(), "Great")).ErrorType);
        Assert.Equal(SocialErrorTypes.PlusRequired, (await Chat(ctx).SendAsync(p[1], room, Guid.NewGuid(), "Plan?")).ErrorType);
        // Plus sends freely in the same room.
        Assert.True((await Chat(ctx, plus: true).SendAsync(p[0], room, Guid.NewGuid(), "Great")).Ok);
        Assert.Equal(ChatLocks.None, (await Chat(ctx, plus: true).RoomAsync(p[0], room))!.Lock);
        Assert.Equal(1, (await ctx.Set<ChatRoomMember>().AsNoTracking().SingleAsync(m => m.RoomId == room && m.UserId == p[1])).SentCount);
    }

    [IntegrationFact]
    public async Task A_plus_starter_keeps_sending_while_the_request_waits()
    {
        var p = await PeopleAsync(2);
        await using var ctx = CreateContext();
        var room = (await Chat(ctx, plus: true).StartAsync(p[0], p[1], Guid.NewGuid(), "Hi, going to Manali too?")).Value!.Room.RoomId;
        Assert.True((await Chat(ctx, plus: true).SendAsync(p[0], room, Guid.NewGuid(), "hello?")).Ok);
        Assert.Equal(ChatLocks.None, (await Chat(ctx, plus: true).RoomAsync(p[0], room))!.Lock);
    }

    [IntegrationFact]
    public async Task Decliner_cannot_message_and_the_starter_still_sees_his_request()
    {
        var p = await PeopleAsync(2);
        await using var ctx = CreateContext();
        var room = (await Chat(ctx).StartAsync(p[0], p[1], Guid.NewGuid(), "Hi, going to Manali too?")).Value!.Room.RoomId;
        Assert.True((await Chat(ctx).DeleteRoomAsync(p[1], room)).Ok);

        var pre = await Chat(ctx).WithUserAsync(p[1], p[0]);
        Assert.True(pre.Ok, pre.ErrorType);
        Assert.False(pre.Value!.CanMessage);
        Assert.Null(pre.Value.Room);
        var start = await Chat(ctx).StartAsync(p[1], p[0], Guid.NewGuid(), "Changed my mind");
        Assert.Equal(SocialErrorTypes.CannotMessage, start.ErrorType);
        Assert.Equal(403, SocialErrorTypes.StatusOf(start.ErrorType!));
        var hisView = (await Chat(ctx).RoomAsync(p[1], room))!;
        Assert.False(hisView.CanSend);
        Assert.NotEqual(ChatLocks.None, hisView.Lock);

        // The starter never learns of the decline: a Free starter sees the same Plus lock as before, a Plus starter waits.
        var starter = (await Chat(ctx).RoomAsync(p[0], room))!;
        Assert.Equal(ChatRoomStatuses.Request, starter.Status);
        Assert.Equal(ChatLocks.Plus, starter.Lock);
        Assert.Equal(ChatLocks.AwaitingReply, (await Chat(ctx, plus: true).RoomAsync(p[0], room))!.Lock);
    }

    [IntegrationFact]
    public async Task Same_client_id_is_one_message_and_masked_text_is_stored()
    {
        var p = await PeopleAsync(2);
        await using var ctx = CreateContext();
        var room = (await Chat(ctx).StartAsync(p[0], p[1], Guid.NewGuid(), "hi")).Value!.Room.RoomId;
        await Chat(ctx).SendAsync(p[1], room, Guid.NewGuid(), "hello");
        var client = Guid.NewGuid();
        var first = await Chat(ctx, plus: true).SendAsync(p[0], room, client, "call 9876543210");
        var again = await Chat(ctx, plus: true).SendAsync(p[0], room, client, "call 9876543210");
        Assert.Equal(first.Value!.Seq, again.Value!.Seq);
        Assert.True(first.Value.Masked);
        var stored = await ctx.Set<ChatMessage>().AsNoTracking().SingleAsync(m => m.RoomId == room && m.Seq == first.Value.Seq);
        Assert.DoesNotContain("9876543210", stored.Body);
    }

    [IntegrationFact]
    public async Task New_chat_cap_and_read_pointer_never_goes_back()
    {
        var p = await PeopleAsync(5);
        await using var ctx = CreateContext();
        for (var i = 1; i <= 3; i++) // batch-loop: three new people, the free monthly cap
            Assert.True((await Chat(ctx).StartAsync(p[0], p[i], Guid.NewGuid(), "hi")).Ok);
        Assert.Equal(ChatLocks.Limit, (await Chat(ctx).WithUserAsync(p[0], p[4])).Value!.Lock);   // only the used-up cap locks a new chat
        Assert.Equal(SocialErrorTypes.NewChatLimit, (await Chat(ctx).StartAsync(p[0], p[4], Guid.NewGuid(), "hi")).ErrorType);

        var room = (await Chat(ctx).WithUserAsync(p[1], p[0])).Value!.Room!.RoomId;
        Assert.True((await Chat(ctx).ReadAsync(p[1], room, 1)).Ok);
        await Chat(ctx).ReadAsync(p[1], room, 0);
        var member = await ctx.Set<ChatRoomMember>().AsNoTracking().SingleAsync(m => m.RoomId == room && m.UserId == p[1]);
        Assert.Equal(1, member.LastReadSeq);
        Assert.Equal(0, member.UnreadCount);
    }
}
