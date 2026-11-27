using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RentNearBy.Core.Entities.Social;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.Buddy.Be2;
using RentNearBy.Infrastructure.Services.Buddy.Be4;
using RentNearBy.Infrastructure.Services.Social;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Support.Buddy;
using RentNearBy.Tests.Support.Social;

namespace RentNearBy.Tests.Social;

// Owner decision 2026-11-27, changed 2026-11-28 (I-9): sending freely in a trip group room needs the SeekerGroupChat switch
// (Free 0, Plus 1, payment off = allowed); without it a member sends ONE message, then the Plus lock. Reading, membership,
// system lines and pushes are never locked.
public class TripGroupChatPlusRulesTests
{
    private static CatalogPlan Plan(Guid id, bool isDefault, Func<string, int> value)
    {
        var features = SeekerPlanKeys.All.ToDictionary(k => k, value);
        return new CatalogPlan(id, PlanFeatures.Seeker, isDefault ? SeekerPlanSeed.FreeCode : SeekerPlanSeed.PlusCode, isDefault ? "Free" : SeekerPlanSeed.PlusName,
            null, isDefault, true, 0, isDefault ? 0 : 1, features, PlanFeatureValues.From(features), Array.Empty<CatalogPrice>(),
            isDefault ? null : SeekerPlanSeed.PlayProductId);
    }

    private static readonly CatalogPlan Free = Plan(SeekerPlanSeed.FreePlanId, true, SeekerPlanKeys.FreeDefault);
    private static readonly CatalogPlan Plus = Plan(SeekerPlanSeed.PlusPlanId, false, SeekerPlanKeys.PlusDefault);

    [Fact]
    public void The_key_is_a_switch_free_0_plus_1()
    {
        Assert.Contains(SeekerPlanKeys.GroupChat, SeekerPlanKeys.All);
        Assert.Equal("SeekerGroupChat", SeekerPlanKeys.GroupChat);
        Assert.Equal((0, 1), (SeekerPlanKeys.FreeDefault(SeekerPlanKeys.GroupChat), SeekerPlanKeys.PlusDefault(SeekerPlanKeys.GroupChat)));
        Assert.Contains(SeekerPlanKeys.GroupChat, SeekerPlanValueRules.Switches);
        Assert.Equal("Trip group chat", SeekerPlanValueRules.Label(SeekerPlanKeys.GroupChat));
        Assert.Equal((0, 1), SeekerPlanValueRules.Range(SeekerPlanKeys.GroupChat));
        Assert.NotNull(SeekerPlanValueRules.KeyError(SeekerPlanKeys.GroupChat, 2));
    }

    [Fact]
    public void Before_during_and_after_plus_and_with_payment_off()
    {
        Assert.False(SeekerLimitsService.Compute([Free, Plus], null, paymentOn: true, held: false).GroupChat);                      // before
        Assert.True(SeekerLimitsService.Compute([Free, Plus], SeekerPlanSeed.PlusPlanId, paymentOn: true, held: false).GroupChat);  // during
        Assert.False(SeekerLimitsService.Compute([Free, Plus], null, paymentOn: true, held: false).GroupChat);                      // after (no entitled row)
        Assert.True(SeekerLimitsService.Compute([Free, Plus], null, paymentOn: false, held: false).GroupChat);                      // payment off
    }

    [Fact]
    public void A_trip_room_without_the_switch_allows_one_message_and_left_still_wins()
    {
        Assert.Equal(ChatLocks.None, SocialChatService.LockOf(ChatKinds.Trip, ChatRoomStatuses.Active, false, 5, false, false, groupChat: false, sentCount: 0));
        Assert.Equal(ChatLocks.Plus, SocialChatService.LockOf(ChatKinds.Trip, ChatRoomStatuses.Active, false, 5, false, false, groupChat: false, sentCount: 1));
        Assert.Equal(ChatLocks.None, SocialChatService.LockOf(ChatKinds.Trip, ChatRoomStatuses.Active, false, 5, false, false, groupChat: true, sentCount: 9));
        // A Plus account without the switch (admin turned it off) is still one message in a trip room.
        Assert.Equal(ChatLocks.Plus, SocialChatService.LockOf(ChatKinds.Trip, ChatRoomStatuses.Active, false, 5, false, true, groupChat: false, sentCount: 1));
        Assert.Equal(ChatLocks.Left, SocialChatService.LockOf(ChatKinds.Trip, ChatRoomStatuses.Active, false, 5, true, false, groupChat: false, sentCount: 1));
        // Direct rooms go by the plan, not by the switch.
        Assert.Equal(ChatLocks.None, SocialChatService.LockOf(ChatKinds.Direct, ChatRoomStatuses.Active, true, 5, false, true, groupChat: false, sentCount: 9));
        Assert.Equal(ChatLocks.Plus, SocialChatService.LockOf(ChatKinds.Direct, ChatRoomStatuses.Active, true, 5, false, false, groupChat: true, sentCount: 1));
    }
}

// Needs RENTNEARBY_TEST_DB; skipped without it.
public class TripGroupChatPlusIntegrationTests(TestDatabase db) : IntegrationTestBase(db)
{
    private static SocialChatService Chat(ApplicationDbContext ctx, bool plus)
        => new(ctx, SocialTestSeams.Limits(10, plus), new NoticeWriter(ctx), SocialTestSeams.AfterCommit(ctx), TimeProvider.System,
            NullLogger<SocialChatService>.Instance);

    private static TripInviteService Invites(ApplicationDbContext ctx)
    {
        var ac = SocialTestSeams.AfterCommit(ctx);
        return new(ctx, new NoticeWriter(ctx), new RecordingLiveHints(), TimeProvider.System, NullLogger<TripInviteService>.Instance,
            SocialTestSeams.Limits(10), SocialTestSeams.GroupChat(ctx, ac), ac);
    }

    private async Task<(Guid Owner, Guid Member, Guid Room)> RoomAsync()
    {
        Guid owner, member, trip, invite;
        await using (var ctx = CreateContext())
        {
            var p = await BuddySeed.UsersAsync(ctx, 2);
            (owner, member) = (p[0], p[1]);
            trip = await BuddySeed.TripAsync(ctx, owner);
            invite = (await BuddySeed.MembersAsync(ctx, trip, owner, [member], TripMemberStatuses.Invited))[0];
            await SocialSeed.ProfileAsync(ctx, owner);
            await SocialSeed.ProfileAsync(ctx, member);
        }
        await using (var ctx = CreateContext()) Assert.True((await Invites(ctx).AcceptAsync(invite, member)).Ok); // a Free user joins
        await using var read = CreateContext();
        var room = (await read.Set<ChatRoom>().AsNoTracking().SingleAsync(r => r.TripId == trip && r.Kind == ChatKinds.Trip)).Id;
        return (owner, member, room);
    }

    [IntegrationFact]
    public async Task An_existing_database_gets_the_key_with_the_seed_defaults_and_an_admin_value_is_kept()
    {
        await using var ctx = CreateContext();
        await DataSeeder.SeedSeekerPlansAsync(ctx);
        await ctx.PlanFeatures.Where(f => f.Key == SeekerPlanKeys.GroupChat
            && (f.PlanId == SeekerPlanSeed.FreePlanId || f.PlanId == SeekerPlanSeed.PlusPlanId)).ExecuteDeleteAsync();
        await DataSeeder.SyncSeekerGroupChatAsync(ctx);
        async Task<int> ValueAsync(Guid plan) => (await ctx.PlanFeatures.AsNoTracking()
            .SingleAsync(f => f.PlanId == plan && f.Key == SeekerPlanKeys.GroupChat)).Value;
        Assert.Equal((0, 1), (await ValueAsync(SeekerPlanSeed.FreePlanId), await ValueAsync(SeekerPlanSeed.PlusPlanId)));
        await ctx.PlanFeatures.Where(f => f.PlanId == SeekerPlanSeed.FreePlanId && f.Key == SeekerPlanKeys.GroupChat)
            .ExecuteUpdateAsync(x => x.SetProperty(f => f.Value, 1));
        try
        {
            await DataSeeder.SyncSeekerGroupChatAsync(ctx);
            Assert.Equal(1, await ValueAsync(SeekerPlanSeed.FreePlanId));
        }
        finally
        {
            await ctx.PlanFeatures.Where(f => f.PlanId == SeekerPlanSeed.FreePlanId && f.Key == SeekerPlanKeys.GroupChat)
                .ExecuteUpdateAsync(x => x.SetProperty(f => f.Value, 0));
        }
    }

    [IntegrationFact]
    public async Task Free_member_sends_one_message_then_the_plus_lock_and_a_second_send_stores_nothing()
    {
        var (owner, member, room) = await RoomAsync();
        await using var ctx = CreateContext();
        var dto = await Chat(ctx, plus: false).RoomAsync(member, room);
        Assert.Equal((ChatLocks.None, true), (dto!.Lock, dto.CanSend));
        var page = await Chat(ctx, plus: false).MessagesAsync(member, room, null, null, null);
        Assert.True(page.Ok, page.ErrorType);
        Assert.Contains(page.Value!.Items, m => m.System == ChatSystemKinds.Joined);

        Assert.True((await Chat(ctx, plus: false).SendAsync(member, room, Guid.NewGuid(), "Hello all")).Ok);
        var after = await Chat(ctx, plus: false).RoomAsync(member, room);
        Assert.Equal((ChatLocks.Plus, false), (after!.Lock, after.CanSend));
        var refused = await Chat(ctx, plus: false).SendAsync(member, room, Guid.NewGuid(), "Anyone?");
        Assert.Equal(SocialErrorTypes.PlusRequired, refused.ErrorType);
        Assert.Equal(403, SocialErrorTypes.StatusOf(refused.ErrorType!));
        Assert.Equal(1, await ctx.Set<ChatMessage>().CountAsync(m => m.RoomId == room && m.SenderId == member));
        // A free host follows the same rule, counted on his own.
        Assert.True((await Chat(ctx, plus: false).SendAsync(owner, room, Guid.NewGuid(), "Hi")).Ok);
        Assert.Equal(SocialErrorTypes.PlusRequired, (await Chat(ctx, plus: false).SendAsync(owner, room, Guid.NewGuid(), "Hi again")).ErrorType);
        // The purge of old messages never gives the message back.
        await ctx.Set<ChatMessage>().Where(m => m.RoomId == room && m.SenderId == member).ExecuteDeleteAsync();
        Assert.Equal(SocialErrorTypes.PlusRequired, (await Chat(ctx, plus: false).SendAsync(member, room, Guid.NewGuid(), "Again")).ErrorType);
    }

    [IntegrationFact]
    public async Task Plus_member_sends_freely_a_free_member_still_gets_the_push_and_after_plus_ends_history_and_membership_stay()
    {
        var (owner, member, room) = await RoomAsync();
        await using (var ctx = CreateContext())
        {
            for (var i = 0; i < 3; i++) // batch-loop: no monthly count on Plus
                Assert.True((await Chat(ctx, plus: true).SendAsync(member, room, Guid.NewGuid(), $"Message {i}")).Ok);
            Assert.True(await ctx.OutboxMessages.AnyAsync(o => o.Key.StartsWith($"chat:{room:N}:{owner:N}:"))); // the free host is told
        }
        await using (var ctx = CreateContext())
        {
            // Plus ended: sending stops with the same prompt; the history and the membership stay.
            Assert.Equal(SocialErrorTypes.PlusRequired, (await Chat(ctx, plus: false).SendAsync(member, room, Guid.NewGuid(), "Still here?")).ErrorType);
            var page = await Chat(ctx, plus: false).MessagesAsync(member, room, null, null, null);
            Assert.Equal(3, page.Value!.Items.Count(m => m.SenderId == member));
            Assert.True(await ctx.Set<ChatRoomMember>().AnyAsync(m => m.RoomId == room && m.UserId == member && m.LeftAt == null));
            // Plus again: sending works at once.
            Assert.True((await Chat(ctx, plus: true).SendAsync(member, room, Guid.NewGuid(), "Back")).Ok);
        }
    }
}
