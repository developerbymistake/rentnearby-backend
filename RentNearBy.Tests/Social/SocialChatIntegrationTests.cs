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
    public async Task One_message_until_reply_then_the_receiver_replies_free_and_activates()
    {
        var p = await PeopleAsync(2);
        await using var ctx = CreateContext();
        var started = await Chat(ctx).StartAsync(p[0], p[1], Guid.NewGuid(), "Hi, going to Manali too?");
        Assert.True(started.Ok, started.ErrorType);
        var room = started.Value!.Room.RoomId;
        Assert.Equal(ChatRoomStatuses.Request, started.Value.Room.Status);
        Assert.Equal(SocialErrorTypes.AwaitingReply, (await Chat(ctx).SendAsync(p[0], room, Guid.NewGuid(), "hello?")).ErrorType);

        var reply = await Chat(ctx).SendAsync(p[1], room, Guid.NewGuid(), "Yes!");
        Assert.True(reply.Ok, reply.ErrorType);
        Assert.Equal(2, reply.Value!.Seq);
        Assert.Equal(ChatRoomStatuses.Active, (await Chat(ctx).RoomAsync(p[0], room))!.Status);
        Assert.True((await Chat(ctx).SendAsync(p[0], room, Guid.NewGuid(), "Great")).Ok);
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

        var starter = (await Chat(ctx).RoomAsync(p[0], room))!;
        Assert.Equal(ChatRoomStatuses.Request, starter.Status);
        Assert.Equal(ChatLocks.AwaitingReply, starter.Lock);
    }

    [IntegrationFact]
    public async Task Same_client_id_is_one_message_and_masked_text_is_stored()
    {
        var p = await PeopleAsync(2);
        await using var ctx = CreateContext();
        var room = (await Chat(ctx).StartAsync(p[0], p[1], Guid.NewGuid(), "hi")).Value!.Room.RoomId;
        await Chat(ctx).SendAsync(p[1], room, Guid.NewGuid(), "hello");
        var client = Guid.NewGuid();
        var first = await Chat(ctx).SendAsync(p[0], room, client, "call 9876543210");
        var again = await Chat(ctx).SendAsync(p[0], room, client, "call 9876543210");
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
        Assert.Equal(SocialErrorTypes.NewChatLimit, (await Chat(ctx).StartAsync(p[0], p[4], Guid.NewGuid(), "hi")).ErrorType);

        var room = (await Chat(ctx).WithUserAsync(p[1], p[0])).Value!.Room!.RoomId;
        Assert.True((await Chat(ctx).ReadAsync(p[1], room, 1)).Ok);
        await Chat(ctx).ReadAsync(p[1], room, 0);
        var member = await ctx.Set<ChatRoomMember>().AsNoTracking().SingleAsync(m => m.RoomId == room && m.UserId == p[1]);
        Assert.Equal(1, member.LastReadSeq);
        Assert.Equal(0, member.UnreadCount);
    }
}
