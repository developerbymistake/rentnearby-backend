using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RentNearBy.Core.DTOs.Social;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Entities.Buddy;
using RentNearBy.Core.Entities.Social;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Repositories;
using RentNearBy.Infrastructure.Services.Buddy;
using RentNearBy.Infrastructure.Services.Buddy.Be2;
using RentNearBy.Infrastructure.Services.Buddy.Be4;
using RentNearBy.Infrastructure.Services.Social;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Support.Buddy;
using RentNearBy.Tests.Support.Social;

namespace RentNearBy.Tests.Social;

// Spec 25: the guidelines gate, every Settings control enforced by the server, and the chat security rules.
// Needs RENTNEARBY_TEST_DB; skipped without it.
public class SocialSafetyIntegrationTests(TestDatabase db) : IntegrationTestBase(db)
{
    private static SocialChatService Chat(ApplicationDbContext ctx, bool plus = false)
        => new(ctx, SocialTestSeams.Limits(10, plus), new NoticeWriter(ctx), SocialTestSeams.AfterCommit(ctx), TimeProvider.System,
            NullLogger<SocialChatService>.Instance);

    private static SocialProfileService Profiles(ApplicationDbContext ctx)
        => new(ctx, SocialTestSeams.Limits(10), new PublicTripReader(ctx, TimeProvider.System), SocialTestSeams.AfterCommit(ctx),
            TimeProvider.System, NullLogger<SocialProfileService>.Instance);

    private static FollowService Follows(ApplicationDbContext ctx)
        => new(ctx, new NoticeWriter(ctx), SocialTestSeams.AfterCommit(ctx), NullLogger<FollowService>.Instance);

    private static MyBuddyService Buddies(ApplicationDbContext ctx) => new(ctx, new RecordingLiveHints(), NullLogger<MyBuddyService>.Instance);

    private static PublicTripService Posts(ApplicationDbContext ctx)
        => new(ctx, new UnitOfWork(ctx), new FakeCityCatalog(), SocialTestSeams.Limits(10), new PublicTripReader(ctx, TimeProvider.System),
            SocialTestSeams.AfterCommit(ctx), new RentNearBy.Infrastructure.Services.Buddy.Be4.NoticeWriter(ctx), TimeProvider.System,
            NullLogger<PublicTripService>.Instance);

    private static CreatePublicTripRequest Post() => new()
    {
        ClientTripId = Guid.NewGuid(), CityKeys = [FakeCityCatalog.Nainital, FakeCityCatalog.Almora], WhenKind = SocialRules.WhenKindWithin,
        WithinMonths = 2, Days = 4, JoinGender = JoinGenders.Anyone, Seats = 2, Text = "Looking for two easy-going trekkers.",
    };

    private async Task<List<Guid>> PeopleAsync(int n)
    {
        await using var ctx = CreateContext();
        var ids = await BuddySeed.UsersAsync(ctx, n);
        foreach (var id in ids) await SocialSeed.ProfileAsync(ctx, id);
        return ids;
    }

    private async Task<Guid> PersonAsync(bool guidelines = true, bool isPrivate = false, string policy = MessagePolicies.Everyone,
        bool notifyMessages = true, bool notifyFollows = true, bool notifyFollowedTrips = true)
    {
        await using var ctx = CreateContext();
        var id = (await BuddySeed.UsersAsync(ctx, 1))[0];
        await SocialSeed.ProfileAsync(ctx, id, isPrivate: isPrivate, guidelines: guidelines, messagePolicy: policy,
            notifyMessages: notifyMessages, notifyFollows: notifyFollows, notifyFollowedTrips: notifyFollowedTrips);
        return id;
    }

    private async Task<int> OutboxAsync(string prefix)
    {
        await using var ctx = CreateContext();
        return await ctx.OutboxMessages.CountAsync(o => o.Key.StartsWith(prefix));
    }

    private static string N(Guid id) => id.ToString("N");

    // ------------------------------------------------------------------ guidelines

    [IntegrationFact]
    public async Task First_chat_message_is_refused_until_the_guidelines_are_accepted_and_nothing_is_stored()
    {
        var starter = await PersonAsync(guidelines: false);
        var other = await PersonAsync();
        await using (var ctx = CreateContext())
        {
            Assert.False((await Profiles(ctx).GetMeAsync(starter)).GuidelinesAccepted);
            var refused = await Chat(ctx).StartAsync(starter, other, Guid.NewGuid(), "Hi, going to Manali too?");
            Assert.Equal(SocialErrorTypes.GuidelinesNotAccepted, refused.ErrorType);
            Assert.Equal(403, SocialErrorTypes.StatusOf(refused.ErrorType!));
            // The preflight (a read) still works and no new chat was charged.
            var pre = await Chat(ctx).WithUserAsync(starter, other);
            Assert.True(pre.Ok, pre.ErrorType);
            Assert.Equal(0, pre.Value!.NewChats.Used);
        }
        await using (var check = CreateContext())
        {
            Assert.Equal(0, await check.Set<ChatRoom>().CountAsync(r => r.RequestedByUserId == starter));
            Assert.Equal(0, await check.Set<ChatMessage>().CountAsync(m => m.SenderId == starter));
        }
        await using (var ctx = CreateContext())
        {
            Assert.True((await Profiles(ctx).AcceptGuidelinesAsync(starter)).Ok);
            Assert.True((await Profiles(ctx).GetMeAsync(starter)).GuidelinesAccepted);
            Assert.True((await Chat(ctx).StartAsync(starter, other, Guid.NewGuid(), "Hi, going to Manali too?")).Ok);
        }
    }

    [IntegrationFact]
    public async Task Accepting_twice_keeps_the_first_time_and_a_new_version_asks_again()
    {
        var me = await PersonAsync(guidelines: false);
        var other = await PersonAsync();
        DateTime? first;
        await using (var ctx = CreateContext())
        {
            Assert.True((await Profiles(ctx).AcceptGuidelinesAsync(me)).Ok);
            first = (await ctx.Set<UserProfile>().AsNoTracking().SingleAsync(p => p.UserId == me)).GuidelinesAcceptedAt;
            Assert.NotNull(first);
        }
        await using (var ctx = CreateContext())
        {
            Assert.True((await Profiles(ctx).AcceptGuidelinesAsync(me)).Ok);
            var p = await ctx.Set<UserProfile>().AsNoTracking().SingleAsync(x => x.UserId == me);
            Assert.Equal(first, p.GuidelinesAcceptedAt);
            Assert.Equal(SocialLimits.GuidelinesVersion, p.GuidelinesVersion);
        }
        // An older stored version (the guidelines were revised since) is not accepted any more.
        await using (var ctx = CreateContext())
        {
            await ctx.Database.ExecuteSqlInterpolatedAsync($"""UPDATE "UserProfiles" SET "GuidelinesVersion" = 0 WHERE "UserId" = {me}""");
            Assert.False((await Profiles(ctx).GetMeAsync(me)).GuidelinesAccepted);
            Assert.Equal(SocialErrorTypes.GuidelinesNotAccepted, (await Chat(ctx).StartAsync(me, other, Guid.NewGuid(), "Hello")).ErrorType);
            Assert.True((await Profiles(ctx).AcceptGuidelinesAsync(me)).Ok);
            Assert.True((await Profiles(ctx).GetMeAsync(me)).GuidelinesAccepted);
        }
    }

    [IntegrationFact]
    public async Task Accepting_without_a_profile_is_profile_required()
    {
        await using var ctx = CreateContext();
        var id = (await BuddySeed.UsersAsync(ctx, 1))[0];
        Assert.Equal(SocialErrorTypes.ProfileRequired, (await Profiles(ctx).AcceptGuidelinesAsync(id)).ErrorType);
        Assert.False((await Profiles(ctx).GetMeAsync(id)).GuidelinesAccepted);
    }

    [IntegrationFact]
    public async Task A_receivers_first_reply_needs_the_guidelines_too()
    {
        var starter = await PersonAsync();
        var receiver = await PersonAsync(guidelines: false);
        Guid room;
        await using (var ctx = CreateContext())
        {
            var started = await Chat(ctx).StartAsync(starter, receiver, Guid.NewGuid(), "Hi, going to Manali too?");
            Assert.True(started.Ok, started.ErrorType);
            room = started.Value!.Room.RoomId;
            // Reading is allowed before acceptance.
            Assert.True((await Chat(ctx).MessagesAsync(receiver, room, null, null, null)).Ok);
            Assert.Equal(SocialErrorTypes.GuidelinesNotAccepted, (await Chat(ctx).SendAsync(receiver, room, Guid.NewGuid(), "Yes!")).ErrorType);
        }
        await using (var check = CreateContext())
        {
            Assert.Equal(ChatRoomStatuses.Request, (await check.Set<ChatRoom>().AsNoTracking().SingleAsync(r => r.Id == room)).Status);
            Assert.Equal(1, await check.Set<ChatMessage>().CountAsync(m => m.RoomId == room));
        }
        await using (var ctx = CreateContext())
        {
            Assert.True((await Profiles(ctx).AcceptGuidelinesAsync(receiver)).Ok);
            Assert.True((await Chat(ctx).SendAsync(receiver, room, Guid.NewGuid(), "Yes!")).Ok);
        }
    }

    // ------------------------------------------------------------------ trip group chat

    private static TripInviteService Invites(ApplicationDbContext ctx)
    {
        var ac = SocialTestSeams.AfterCommit(ctx);
        return new(ctx, new NoticeWriter(ctx), new RecordingLiveHints(), TimeProvider.System, NullLogger<TripInviteService>.Instance,
            SocialTestSeams.Limits(10), SocialTestSeams.GroupChat(ctx, ac), ac);
    }

    [IntegrationFact]
    public async Task Trip_group_send_needs_the_guidelines_and_masks_contact_details()
    {
        Guid owner, member, trip, invite;
        await using (var ctx = CreateContext())
        {
            var p = await BuddySeed.UsersAsync(ctx, 2);
            owner = p[0];
            member = p[1];
            trip = await BuddySeed.TripAsync(ctx, owner);
            invite = (await BuddySeed.MembersAsync(ctx, trip, owner, [member], TripMemberStatuses.Invited))[0];
            await SocialSeed.ProfileAsync(ctx, owner);
            await SocialSeed.ProfileAsync(ctx, member, guidelines: false);
        }
        await using (var ctx = CreateContext()) Assert.True((await Invites(ctx).AcceptAsync(invite, member)).Ok);
        Guid room;
        await using (var ctx = CreateContext())
            room = (await ctx.Set<ChatRoom>().AsNoTracking().SingleAsync(r => r.TripId == trip && r.Kind == ChatKinds.Trip)).Id;

        await using (var ctx = CreateContext())
        {
            // Trip group sends need Plus (owner 2026-11-27); the guidelines gate still comes first.
            Assert.Equal(SocialErrorTypes.GuidelinesNotAccepted, (await Chat(ctx, plus: true).SendAsync(member, room, Guid.NewGuid(), "Hello all")).ErrorType);
            Assert.True((await Chat(ctx).MessagesAsync(member, room, null, null, null)).Ok);
            Assert.True((await Chat(ctx, plus: true).SendAsync(owner, room, Guid.NewGuid(), "Welcome")).Ok);
            Assert.True((await Profiles(ctx).AcceptGuidelinesAsync(member)).Ok);
            var sent = await Chat(ctx, plus: true).SendAsync(member, room, Guid.NewGuid(), "call me 9876543210");
            Assert.True(sent.Ok, sent.ErrorType);
            Assert.True(sent.Value!.Masked);
            Assert.DoesNotContain("9876543210", sent.Value.Text);
        }
    }

    // ------------------------------------------------------------------ settings: who can message

    [IntegrationFact]
    public async Task Message_policy_following_lets_only_people_he_follows_start_a_chat()
    {
        var target = await PersonAsync(policy: MessagePolicies.Following);
        var stranger = await PersonAsync();
        await using var ctx = CreateContext();
        Assert.False((await Chat(ctx).WithUserAsync(stranger, target)).Value!.CanMessage);
        Assert.Equal(SocialErrorTypes.CannotMessage, (await Chat(ctx).StartAsync(stranger, target, Guid.NewGuid(), "Hi")).ErrorType);
        Assert.True((await Follows(ctx).FollowAsync(target, stranger)).Ok);
        Assert.True((await Chat(ctx).WithUserAsync(stranger, target)).Value!.CanMessage);
        Assert.True((await Chat(ctx).StartAsync(stranger, target, Guid.NewGuid(), "Hi")).Ok);
    }

    [IntegrationFact]
    public async Task Message_policy_buddies_lets_only_my_buddies_start_a_chat()
    {
        var target = await PersonAsync(policy: MessagePolicies.Buddies);
        var friend = await PersonAsync();
        await using var ctx = CreateContext();
        Assert.Equal(SocialErrorTypes.CannotMessage, (await Chat(ctx).StartAsync(friend, target, Guid.NewGuid(), "Hi")).ErrorType);
        Assert.True((await Follows(ctx).FollowAsync(friend, target)).Ok);
        // He follows me back is not enough for Buddies: he must be in my My Buddies.
        Assert.True((await Follows(ctx).FollowAsync(target, friend)).Ok);
        Assert.Equal(SocialErrorTypes.CannotMessage, (await Chat(ctx).StartAsync(friend, target, Guid.NewGuid(), "Hi")).ErrorType);
        Assert.True((await Buddies(ctx).AddAsync(target, friend)).Ok);
        Assert.True((await Chat(ctx).StartAsync(friend, target, Guid.NewGuid(), "Hi")).Ok);
    }

    [IntegrationFact]
    public async Task Message_policy_everyone_lets_anyone_start_a_chat()
    {
        var target = await PersonAsync();
        var stranger = await PersonAsync();
        await using var ctx = CreateContext();
        Assert.True((await Chat(ctx).StartAsync(stranger, target, Guid.NewGuid(), "Hi")).Ok);
    }

    // ------------------------------------------------------------------ settings: notifications

    [IntegrationFact]
    public async Task Message_push_is_skipped_when_the_receiver_turned_messages_off()
    {
        var sender = await PersonAsync();
        var quiet = await PersonAsync(notifyMessages: false);
        var loud = await PersonAsync();
        Guid quietRoom, loudRoom;
        await using (var ctx = CreateContext())
        {
            quietRoom = (await Chat(ctx).StartAsync(sender, quiet, Guid.NewGuid(), "Hi")).Value!.Room.RoomId;
            loudRoom = (await Chat(ctx).StartAsync(sender, loud, Guid.NewGuid(), "Hi")).Value!.Room.RoomId;
        }
        Assert.Equal(0, await OutboxAsync($"chat:{N(quietRoom)}:{N(quiet)}:"));
        Assert.Equal(1, await OutboxAsync($"chat:{N(loudRoom)}:{N(loud)}:"));
    }

    [IntegrationFact]
    public async Task Follow_push_is_skipped_when_the_person_turned_follows_off()
    {
        var quiet = await PersonAsync(notifyFollows: false);
        var loud = await PersonAsync();
        var fan = await PersonAsync();
        await using (var ctx = CreateContext())
        {
            Assert.True((await Follows(ctx).FollowAsync(fan, quiet)).Ok);
            Assert.True((await Follows(ctx).FollowAsync(fan, loud)).Ok);
        }
        Assert.Equal(0, await OutboxAsync($"fol:{N(quiet)}:"));
        Assert.Equal(1, await OutboxAsync($"fol:{N(loud)}:"));
    }

    [IntegrationFact]
    public async Task Follow_request_push_is_skipped_when_follows_are_off_on_a_private_profile()
    {
        var quiet = await PersonAsync(isPrivate: true, notifyFollows: false);
        var loud = await PersonAsync(isPrivate: true);
        var fan = await PersonAsync();
        await using (var ctx = CreateContext())
        {
            Assert.Equal(FollowStates.Requested, (await Follows(ctx).FollowAsync(fan, quiet)).Value!.State);
            Assert.Equal(FollowStates.Requested, (await Follows(ctx).FollowAsync(fan, loud)).Value!.State);
        }
        Assert.Equal(0, await OutboxAsync($"folreq:{N(fan)}:{N(quiet)}"));
        Assert.Equal(1, await OutboxAsync($"folreq:{N(fan)}:{N(loud)}"));
    }

    [IntegrationFact]
    public async Task Followed_trip_notice_goes_only_to_followers_who_kept_the_toggle_on()
    {
        var poster = await PersonAsync();
        var on = await PersonAsync();
        var off = await PersonAsync(notifyFollowedTrips: false);
        Guid trip;
        await using (var ctx = CreateContext())
        {
            Assert.True((await Follows(ctx).FollowAsync(on, poster)).Ok);
            Assert.True((await Follows(ctx).FollowAsync(off, poster)).Ok);
            var created = await Posts(ctx).CreateAsync(poster, Post());
            Assert.True(created.Ok, created.ErrorType);
            trip = created.Value!.TripId;
        }
        await using var check = CreateContext();
        var told = await check.Set<UserNotice>().AsNoTracking().Where(n => n.Kind == NoticeKinds.FollowedPosted && n.TripId == trip)
            .Select(n => n.UserId).ToListAsync();
        Assert.Equal(new[] { on }, told);
    }

    // ------------------------------------------------------------------ settings: private profile and blocked people

    [IntegrationFact]
    public async Task Private_profile_hides_its_trip_posts_from_non_followers_until_a_request_is_accepted()
    {
        var owner = await PersonAsync(isPrivate: true);
        var viewer = await PersonAsync();
        Guid trip;
        await using (var ctx = CreateContext())
        {
            var created = await Posts(ctx).CreateAsync(owner, Post());
            Assert.True(created.Ok, created.ErrorType);
            trip = created.Value!.TripId;
            Assert.Equal(FollowStates.Requested, (await Follows(ctx).FollowAsync(viewer, owner)).Value!.State);
        }
        await using (var read = CreateContext())
            Assert.DoesNotContain((await new PublicTripReader(read, TimeProvider.System).FeedAsync(viewer, null, 20, null, null, null, null, default)).Items,
                x => x.TripId == trip);
        await using (var ctx = CreateContext())
            Assert.True((await Follows(ctx).AcceptRequestAsync(owner, viewer)).Ok);
        await using (var read = CreateContext())
            Assert.Contains((await new PublicTripReader(read, TimeProvider.System).FeedAsync(viewer, null, 20, null, null, null, null, default)).Items,
                x => x.TripId == trip);
    }

    [IntegrationFact]
    public async Task Switching_private_off_accepts_waiting_requests()
    {
        var owner = await PersonAsync(isPrivate: true);
        var fan = await PersonAsync();
        await using var ctx = CreateContext();
        Assert.Equal(FollowStates.Requested, (await Follows(ctx).FollowAsync(fan, owner)).Value!.State);
        Assert.True((await Profiles(ctx).UpdatePrivacyAsync(owner, new UpdatePrivacyRequest { IsPrivate = false })).Ok);
        await using var check = CreateContext();
        Assert.Equal(FollowStatuses.Accepted, (await check.Set<Follow>().AsNoTracking().SingleAsync(f => f.FollowerId == fan && f.FolloweeId == owner)).Status);
    }

    [IntegrationFact]
    public async Task A_blocked_person_cannot_start_a_chat_either_way_and_unblock_brings_it_back()
    {
        var p = await PeopleAsync(2);
        var blockId = Guid.NewGuid();
        await using (var ctx = CreateContext())
        {
            ctx.UserBlocks.Add(new UserBlock { Id = blockId, BlockerId = p[0], BlockedId = p[1], CreatedAt = DateTime.UtcNow });
            await ctx.SaveChangesAsync();
            Assert.Equal(1, (await Profiles(ctx).GetMeAsync(p[0])).Counts.Blocked);
            Assert.Equal(SocialErrorTypes.PersonNotFound, (await Chat(ctx).StartAsync(p[1], p[0], Guid.NewGuid(), "Hi")).ErrorType);
            Assert.Equal(SocialErrorTypes.PersonNotFound, (await Chat(ctx).StartAsync(p[0], p[1], Guid.NewGuid(), "Hi")).ErrorType);
        }
        await using (var ctx = CreateContext())
        {
            ctx.UserBlocks.Remove(await ctx.UserBlocks.SingleAsync(b => b.Id == blockId));
            await ctx.SaveChangesAsync();
            Assert.Equal(0, (await Profiles(ctx).GetMeAsync(p[0])).Counts.Blocked);
            Assert.True((await Chat(ctx).StartAsync(p[1], p[0], Guid.NewGuid(), "Hi")).Ok);
        }
    }

    [IntegrationFact]
    public async Task A_send_into_a_blocked_room_is_acked_but_never_reaches_the_blocker()
    {
        var p = await PeopleAsync(2);
        Guid room;
        await using (var ctx = CreateContext())
        {
            room = (await Chat(ctx).StartAsync(p[0], p[1], Guid.NewGuid(), "Hi, going to Manali too?")).Value!.Room.RoomId;
            Assert.True((await Chat(ctx).SendAsync(p[1], room, Guid.NewGuid(), "Yes!")).Ok);
            Assert.True((await Chat(ctx).ReadAsync(p[0], room, 99)).Ok);
            ctx.UserBlocks.Add(new UserBlock { Id = Guid.NewGuid(), BlockerId = p[0], BlockedId = p[1], CreatedAt = DateTime.UtcNow });
            await ctx.SaveChangesAsync();
            await new SocialBlockParticipant(ctx).OnBlockedAsync(p[0], p[1]);
        }
        await using (var ctx = CreateContext())
            Assert.True((await Chat(ctx).SendAsync(p[1], room, Guid.NewGuid(), "Are you there?")).Ok);
        await using var check = CreateContext();
        Assert.NotNull((await check.Set<ChatRoom>().AsNoTracking().SingleAsync(r => r.Id == room)).BlockedAt);
        Assert.Equal(0, (await check.Set<ChatRoomMember>().AsNoTracking().SingleAsync(m => m.RoomId == room && m.UserId == p[0])).UnreadCount);
    }

    // ------------------------------------------------------------------ settings: saved state shows after reload

    [IntegrationFact]
    public async Task Saved_settings_read_back_and_an_absent_field_keeps_its_value()
    {
        var me = await PersonAsync();
        await using (var ctx = CreateContext())
            Assert.True((await Profiles(ctx).UpdatePrivacyAsync(me, new UpdatePrivacyRequest
            {
                IsPrivate = true, MessagePolicy = MessagePolicies.Buddies, NotifyMessages = false, NotifyFollows = false, NotifyFollowedTrips = false,
            })).Ok);
        await using (var ctx = CreateContext())
            Assert.True((await Profiles(ctx).UpdatePrivacyAsync(me, new UpdatePrivacyRequest { NotifyFollows = true })).Ok);
        await using var check = CreateContext();
        var profile = (await Profiles(check).GetMeAsync(me)).Profile!;
        Assert.True(profile.IsPrivate);
        Assert.Equal(MessagePolicies.Buddies, profile.MessagePolicy);
        Assert.False(profile.NotifyMessages);
        Assert.True(profile.NotifyFollows);
        Assert.False(profile.NotifyFollowedTrips);
    }

    [IntegrationFact]
    public async Task A_bad_message_policy_is_refused_and_changes_nothing()
    {
        var me = await PersonAsync();
        await using var ctx = CreateContext();
        Assert.Equal(SocialErrorTypes.InvalidProfile,
            (await Profiles(ctx).UpdatePrivacyAsync(me, new UpdatePrivacyRequest { MessagePolicy = "Nobody", IsPrivate = true })).ErrorType);
        Assert.False((await Profiles(ctx).GetMeAsync(me)).Profile!.IsPrivate);
    }

    // ------------------------------------------------------------------ chat security

    [IntegrationFact]
    public async Task Only_members_read_or_send_in_a_room_and_text_over_500_is_refused_with_nothing_stored()
    {
        var p = await PeopleAsync(3);
        await using var ctx = CreateContext();
        var room = (await Chat(ctx).StartAsync(p[0], p[1], Guid.NewGuid(), "Hi, going to Manali too?")).Value!.Room.RoomId;
        Assert.True((await Chat(ctx).SendAsync(p[1], room, Guid.NewGuid(), "Yes!")).Ok);

        Assert.Equal(SocialErrorTypes.RoomNotFound, (await Chat(ctx).MessagesAsync(p[2], room, null, null, null)).ErrorType);
        Assert.Equal(SocialErrorTypes.RoomNotFound, (await Chat(ctx).SendAsync(p[2], room, Guid.NewGuid(), "let me in")).ErrorType);
        Assert.Equal(SocialErrorTypes.RoomNotFound, (await Chat(ctx).ReadAsync(p[2], room, 5)).ErrorType);
        Assert.Null(await Chat(ctx).RoomAsync(p[2], room));

        Assert.Equal(SocialErrorTypes.TextTooLong, (await Chat(ctx).SendAsync(p[0], room, Guid.NewGuid(), new string('a', SocialLimits.ChatTextMax + 1))).ErrorType);
        Assert.Equal(2, await ctx.Set<ChatMessage>().CountAsync(m => m.RoomId == room));
        Assert.True((await Chat(ctx).SendAsync(p[0], room, Guid.NewGuid(), new string('a', SocialLimits.ChatTextMax))).Ok);
        Assert.Equal(3, await ctx.Set<ChatMessage>().CountAsync(m => m.RoomId == room));
    }

    [IntegrationFact]
    public async Task Email_and_link_are_masked_in_the_stored_text()
    {
        var p = await PeopleAsync(2);
        await using var ctx = CreateContext();
        var room = (await Chat(ctx).StartAsync(p[0], p[1], Guid.NewGuid(), "mail me abc@gmail.com or see https://example.org/x")).Value!.Room.RoomId;
        var stored = await ctx.Set<ChatMessage>().AsNoTracking().SingleAsync(m => m.RoomId == room && m.Seq == 1);
        Assert.True(stored.Masked);
        Assert.DoesNotContain("abc@gmail.com", stored.Body);
        Assert.DoesNotContain("example.org", stored.Body);
    }

    // ------------------------------------------------------------------ settings proof: links the first pass did not cover

    [IntegrationFact]
    public async Task Follow_accepted_push_is_skipped_when_the_requester_turned_follows_off()
    {
        var owner = await PersonAsync(isPrivate: true);
        var quiet = await PersonAsync(notifyFollows: false);
        var loud = await PersonAsync();
        await using (var ctx = CreateContext())
        {
            Assert.Equal(FollowStates.Requested, (await Follows(ctx).FollowAsync(quiet, owner)).Value!.State);
            Assert.Equal(FollowStates.Requested, (await Follows(ctx).FollowAsync(loud, owner)).Value!.State);
        }
        await using (var ctx = CreateContext())
        {
            Assert.True((await Follows(ctx).AcceptRequestAsync(owner, quiet)).Ok);
            Assert.True((await Follows(ctx).AcceptRequestAsync(owner, loud)).Ok);
        }
        Assert.Equal(0, await OutboxAsync($"folacc:{N(owner)}:{N(quiet)}"));
        Assert.Equal(1, await OutboxAsync($"folacc:{N(owner)}:{N(loud)}"));
    }

    [IntegrationFact]
    public async Task Trip_group_message_push_skips_members_who_turned_messages_off()
    {
        Guid owner, quiet, loud, trip;
        List<Guid> invites;
        await using (var ctx = CreateContext())
        {
            var p = await BuddySeed.UsersAsync(ctx, 3);
            (owner, quiet, loud) = (p[0], p[1], p[2]);
            trip = await BuddySeed.TripAsync(ctx, owner);
            invites = await BuddySeed.MembersAsync(ctx, trip, owner, [quiet, loud], TripMemberStatuses.Invited);
            await SocialSeed.ProfileAsync(ctx, owner);
            await SocialSeed.ProfileAsync(ctx, quiet, notifyMessages: false);
            await SocialSeed.ProfileAsync(ctx, loud);
        }
        await using (var ctx = CreateContext()) Assert.True((await Invites(ctx).AcceptAsync(invites[0], quiet)).Ok);
        await using (var ctx = CreateContext()) Assert.True((await Invites(ctx).AcceptAsync(invites[1], loud)).Ok);
        Guid room;
        await using (var ctx = CreateContext())
            room = (await ctx.Set<ChatRoom>().AsNoTracking().SingleAsync(r => r.TripId == trip && r.Kind == ChatKinds.Trip)).Id;
        await using (var ctx = CreateContext())
        {
            var sent = await Chat(ctx, plus: true).SendAsync(owner, room, Guid.NewGuid(), "Bus leaves at 7");
            Assert.True(sent.Ok, sent.ErrorType);
        }
        Assert.Equal(0, await OutboxAsync($"chat:{N(room)}:{N(quiet)}:"));
        Assert.Equal(1, await OutboxAsync($"chat:{N(room)}:{N(loud)}:"));
    }

    [IntegrationFact]
    public async Task Private_profile_locks_the_traveller_page_the_post_link_and_join_requests_for_non_followers()
    {
        var owner = await PersonAsync(isPrivate: true);
        var viewer = await PersonAsync();
        Guid trip;
        await using (var ctx = CreateContext())
        {
            var created = await Posts(ctx).CreateAsync(owner, Post());
            Assert.True(created.Ok, created.ErrorType);
            trip = created.Value!.TripId;
        }
        await using (var ctx = CreateContext())
        {
            var page = (await Profiles(ctx).TravellerAsync(viewer, owner))!;
            Assert.True(page.PrivateLocked);
            Assert.Empty(page.PublicTrips);
            Assert.Equal(string.Empty, page.About);
            Assert.Null(await new PublicTripReader(ctx, TimeProvider.System).ByIdAsync(viewer, trip, default));
            var ac = SocialTestSeams.AfterCommit(ctx);
            var joins = new JoinRequestService(ctx, SocialTestSeams.Limits(10), new NoticeWriter(ctx), new RecordingLiveHints(),
                SocialTestSeams.GroupChat(ctx, ac), new PublicTripReader(ctx, TimeProvider.System), ac, TimeProvider.System,
                NullLogger<JoinRequestService>.Instance);
            var ask = await joins.SendAsync(viewer, trip, new SendJoinRequest { ClientRequestId = Guid.NewGuid() });
            Assert.Equal(SocialErrorTypes.TripPostNotFound, ask.ErrorType);
            Assert.Equal(FollowStates.Requested, (await Follows(ctx).FollowAsync(viewer, owner)).Value!.State);
        }
        await using (var ctx = CreateContext())
            Assert.True((await Follows(ctx).AcceptRequestAsync(owner, viewer)).Ok);
        await using (var ctx = CreateContext())
        {
            var page = (await Profiles(ctx).TravellerAsync(viewer, owner))!;
            Assert.False(page.PrivateLocked);
            Assert.Contains(page.PublicTrips, x => x.TripId == trip);
            Assert.NotNull(await new PublicTripReader(ctx, TimeProvider.System).ByIdAsync(viewer, trip, default));
        }
    }

    [IntegrationFact]
    public async Task Block_and_unblock_through_the_block_service_close_and_reopen_the_direct_room()
    {
        var p = await PeopleAsync(2);
        Guid room;
        await using (var ctx = CreateContext())
        {
            room = (await Chat(ctx).StartAsync(p[0], p[1], Guid.NewGuid(), "Hi, going to Manali too?")).Value!.Room.RoomId;
            Assert.True((await Chat(ctx).SendAsync(p[1], room, Guid.NewGuid(), "Yes!")).Ok);
            Assert.True((await Chat(ctx).ReadAsync(p[0], room, 99)).Ok);
        }
        await using (var ctx = CreateContext())
            Assert.Equal(RentNearBy.Core.Interfaces.Buddy.BlockResult.Done, (await BlockService(ctx).BlockAsync(p[0], p[1])).Result);
        await using (var ctx = CreateContext())
        {
            Assert.NotNull((await ctx.Set<ChatRoom>().AsNoTracking().SingleAsync(r => r.Id == room)).BlockedAt);
            Assert.Equal(1, (await Profiles(ctx).GetMeAsync(p[0])).Counts.Blocked);
        }
        await using (var ctx = CreateContext())
            Assert.Equal(RentNearBy.Core.Interfaces.Buddy.BlockResult.Done, (await BlockService(ctx).UnblockAsync(p[0], p[1])).Result);
        await using (var ctx = CreateContext())
        {
            Assert.Null((await ctx.Set<ChatRoom>().AsNoTracking().SingleAsync(r => r.Id == room)).BlockedAt);
            Assert.Equal(0, (await Profiles(ctx).GetMeAsync(p[0])).Counts.Blocked);
            Assert.True((await Chat(ctx).SendAsync(p[1], room, Guid.NewGuid(), "Back again")).Ok);
        }
        await using var check = CreateContext();
        Assert.Equal(1, (await check.Set<ChatRoomMember>().AsNoTracking().SingleAsync(m => m.RoomId == room && m.UserId == p[0])).UnreadCount);
    }

    private static RentNearBy.Infrastructure.Services.Buddy.Be1.UserBlockService BlockService(ApplicationDbContext ctx)
        => new(ctx, [new SocialBlockParticipant(ctx)], new RentNearBy.Tests.Buddy.Be1.FakeTripClosure(), new RecordingLiveHints(),
            NullLogger<RentNearBy.Infrastructure.Services.Buddy.Be1.UserBlockService>.Instance);
}
