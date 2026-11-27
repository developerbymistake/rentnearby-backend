using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Geometries;
using RentNearBy.Core.DTOs.Social;
using RentNearBy.Core.Entities.Buddy;
using RentNearBy.Core.Entities.Social;
using RentNearBy.Core.Interfaces.Social;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Repositories;
using RentNearBy.Infrastructure.Services.Buddy.Be4;
using RentNearBy.Infrastructure.Services.Social;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Support.Buddy;
using RentNearBy.Tests.Support.Social;

namespace RentNearBy.Tests.Social;

// Post notify (owner 2026-11-27, Buddy_resume/post_notify/SPEC.md use cases 1-16, 18, 21, 25-31) end to end on PostGIS.
// Needs RENTNEARBY_TEST_DB; skipped without it.
public class PostNotifyIntegrationTests(TestDatabase db) : IntegrationTestBase(db)
{
    private sealed class FixedClock(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
    }

    // Tomorrow 08:00 IST: every post made in the test is before that day's 07:00 IST cutoff.
    private static DateTime DigestMorning => IstTime.AtUtc(IstTime.TodayIst(DateTime.UtcNow).AddDays(1), 8);

    private static PublicTripService Posts(ApplicationDbContext ctx, ISeekerLimits? limits = null)
        => new(ctx, new UnitOfWork(ctx), new FakeCityCatalog(), limits ?? SocialTestSeams.Limits(10, plus: true),
            new PublicTripReader(ctx, TimeProvider.System), SocialTestSeams.AfterCommit(ctx), new NoticeWriter(ctx), TimeProvider.System,
            NullLogger<PublicTripService>.Instance);

    private static FollowService Follows(ApplicationDbContext ctx)
        => new(ctx, new NoticeWriter(ctx), SocialTestSeams.AfterCommit(ctx), NullLogger<FollowService>.Instance);

    private static SocialProfileService Profiles(ApplicationDbContext ctx)
        => new(ctx, SocialTestSeams.Limits(10), new PublicTripReader(ctx, TimeProvider.System), SocialTestSeams.AfterCommit(ctx),
            TimeProvider.System, NullLogger<SocialProfileService>.Instance);

    private static JoinRequestService Joins(ApplicationDbContext ctx)
    {
        var ac = SocialTestSeams.AfterCommit(ctx);
        return new JoinRequestService(ctx, SocialTestSeams.Limits(10, plus: true), new NoticeWriter(ctx), new RecordingLiveHints(),
            SocialTestSeams.GroupChat(ctx, ac), new PublicTripReader(ctx, TimeProvider.System), ac, TimeProvider.System,
            NullLogger<JoinRequestService>.Instance);
    }

    private static NearbyDigestJob Digest(ApplicationDbContext ctx, DateTime utc)
        => new(ctx, new NoticeWriter(ctx), new FixedClock(utc), NullLogger<NearbyDigestJob>.Instance);

    private static CreatePublicTripRequest Post(string joinGender = JoinGenders.Anyone, bool nearby = false) => new()
    {
        ClientTripId = Guid.NewGuid(), CityKeys = [FakeCityCatalog.Nainital], WhenKind = SocialRules.WhenKindWithin,
        WithinMonths = 2, Days = 3, JoinGender = joinGender, Seats = 4, Text = "Easy hill walks and good food.", NotifyNearby = nearby,
    };

    private static UpdatePublicPostRequest Edit(bool nearby = false) => new()
    {
        WhenKind = SocialRules.WhenKindWithin, WithinMonths = 2, Days = 3, JoinGender = JoinGenders.Anyone, Seats = 4,
        Text = "Easy hill walks and good food.", NotifyNearby = nearby,
    };

    private async Task<Guid> PersonAsync(string gender = SocialGenders.Male, bool isPrivate = false, bool notifyFollowedTrips = true,
        bool oldAccount = true)
    {
        await using var ctx = CreateContext();
        var id = (await BuddySeed.UsersAsync(ctx, 1))[0];
        await SocialSeed.ProfileAsync(ctx, id, gender, isPrivate: isPrivate, notifyFollowedTrips: notifyFollowedTrips);
        if (oldAccount) await ctx.Users.Where(u => u.Id == id).ExecuteUpdateAsync(s => s.SetProperty(u => u.CreatedAt, DateTime.UtcNow.AddDays(-3)));
        return id;
    }

    private async Task<Guid> DistrictAsync(double lat, double lng)
    {
        await using var ctx = CreateContext();
        var ring = new LinearRing(
        [
            new Coordinate(lng - 0.2, lat - 0.2), new Coordinate(lng + 0.2, lat - 0.2), new Coordinate(lng + 0.2, lat + 0.2),
            new Coordinate(lng - 0.2, lat + 0.2), new Coordinate(lng - 0.2, lat - 0.2),
        ]);
        var d = new District
        {
            Id = Guid.NewGuid(), Name = "Notify " + Guid.NewGuid().ToString("N")[..8], StateName = "Test", IsActive = false,
            CreatedAt = DateTime.UtcNow, Boundary = new Polygon(ring) { SRID = 4326 },
        };
        ctx.Districts.Add(d);
        await ctx.SaveChangesAsync();
        return d.Id;
    }

    // The digest reads every area and every waiting post of the shared test database: start each digest test from a clean
    // state (classes of the integration collection run one after another).
    private async Task ResetDigestStateAsync()
    {
        await using var ctx = CreateContext();
        await ctx.Database.ExecuteSqlRawAsync("""UPDATE "UserProfiles" SET "LastDistrictId" = NULL WHERE "LastDistrictId" IS NOT NULL""");
        await ctx.Database.ExecuteSqlRawAsync("""UPDATE "Trips" SET "NearbyNotifiedAt" = now() WHERE "NotifyNearby" AND "NearbyNotifiedAt" IS NULL""");
    }

    private async Task AreaAsync(Guid me, Guid district, TimeSpan? age = null)
    {
        await using var ctx = CreateContext();
        Assert.True((await Profiles(ctx).SetAreaAsync(me, district)).Ok);
        if (age is { } a)
            await ctx.Set<UserProfile>().Where(p => p.UserId == me).ExecuteUpdateAsync(s => s.SetProperty(p => p.LastDistrictAt, DateTime.UtcNow - a));
    }

    private async Task FollowAsync(Guid follower, Guid followee)
    {
        await using var ctx = CreateContext();
        Assert.True((await Follows(ctx).FollowAsync(follower, followee)).Ok);
    }

    private async Task<PublicTripDto> CreateAsync(Guid poster, CreatePublicTripRequest r, ISeekerLimits? limits = null)
    {
        await using var ctx = CreateContext();
        var created = await Posts(ctx, limits).CreateAsync(poster, r);
        Assert.True(created.Ok, created.ErrorType);
        return created.Value!;
    }

    // No post lock any more (owner 2026-11-28, I-3): the owner makes a post private at once.
    private static async Task UnpublishAfterLockAsync(ApplicationDbContext ctx, Guid owner, Guid tripId)
    {
        var done = await Posts(ctx).UnpublishAsync(owner, tripId);
        Assert.True(done.Ok, done.ErrorType);
    }

    private async Task<int> OutboxAsync(string prefix)
    {
        await using var ctx = CreateContext();
        return await ctx.OutboxMessages.CountAsync(o => o.Key.StartsWith(prefix));
    }

    private async Task<string> BodyAsync(string key)
    {
        await using var ctx = CreateContext();
        var payload = await ctx.OutboxMessages.Where(o => o.Key == key).Select(o => o.Payload).SingleAsync();
        return JsonSerializer.Deserialize<OutboxPushPayload>(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Body;
    }

    private async Task<List<Guid>> NoticedAsync(string kind, Guid trip)
    {
        await using var ctx = CreateContext();
        return await ctx.Set<UserNotice>().AsNoTracking().Where(n => n.Kind == kind && n.TripId == trip).Select(n => n.UserId).ToListAsync();
    }

    private static string N(Guid id) => id.ToString("N");

    private static string Day => IstTime.TodayIst(DigestMorning).ToString("yyyyMMdd");

    // ------------------------------------------------------------------ followers (use cases 1, 4, 6, 7)

    [IntegrationFact]
    public async Task Followers_get_one_immediate_push_and_a_bell_row_only_when_every_rule_allows()
    {
        var poster = await PersonAsync();
        var man = await PersonAsync();
        var woman = await PersonAsync(SocialGenders.Female);
        var switchedOff = await PersonAsync(notifyFollowedTrips: false);
        var blocked = await PersonAsync();
        var disabled = await PersonAsync();
        foreach (var f in new[] { man, woman, switchedOff, blocked, disabled }) await FollowAsync(f, poster); // batch-loop
        await using (var ctx = CreateContext())
        {
            ctx.UserBlocks.Add(new UserBlock { Id = Guid.NewGuid(), BlockerId = poster, BlockedId = blocked, CreatedAt = DateTime.UtcNow });
            await ctx.SaveChangesAsync();
            await ctx.Users.Where(u => u.Id == disabled).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
        }

        var post = await CreateAsync(poster, Post(JoinGenders.Men));
        Assert.Equal((PostNotifyStates.Sent, PostNotifyStates.Off, 1), (post.Notify!.Followers, post.Notify.Nearby, post.Notify.Used));
        Assert.Equal([man], await NoticedAsync(NoticeKinds.FollowedPosted, post.TripId));
        Assert.Equal(1, await OutboxAsync($"tpost:{N(poster)}:{N(man)}:"));
        foreach (var f in new[] { woman, switchedOff, blocked, disabled })
            Assert.Equal(0, await OutboxAsync($"tpost:{N(poster)}:{N(f)}:")); // batch-loop
        Assert.Equal("Person 1 posted a trip", await BodyAsync((await FirstKeyAsync($"tpost:{N(poster)}:{N(man)}:"))!));
    }

    private async Task<string?> FirstKeyAsync(string prefix)
    {
        await using var ctx = CreateContext();
        return await ctx.OutboxMessages.Where(o => o.Key.StartsWith(prefix)).Select(o => o.Key).FirstOrDefaultAsync();
    }

    // ------------------------------------------------------------------ re-publish (use cases 9, 28)

    [IntegrationFact]
    public async Task A_re_publish_never_notifies_again_and_keeps_its_feed_place()
    {
        var poster = await PersonAsync();
        var fan = await PersonAsync();
        await FollowAsync(fan, poster);
        var post = await CreateAsync(poster, Post());
        await using (var ctx = CreateContext()) await UnpublishAfterLockAsync(ctx, poster, post.TripId);
        await using (var ctx = CreateContext())
        {
            var again = await Posts(ctx).PutPostAsync(poster, post.TripId, Edit(nearby: true));
            Assert.True(again.Ok, again.ErrorType);
            Assert.Equal((PostNotifyStates.AlreadySent, PostNotifyStates.AlreadySent), (again.Value!.Notify!.Followers, again.Value.Notify.Nearby));
            Assert.Equal(post.PublishedAt, again.Value.PublishedAt);
        }
        await using (var ctx = CreateContext())
        {
            var edit = await Posts(ctx).PutPostAsync(poster, post.TripId, Edit());
            Assert.Null(edit.Value!.Notify); // an edit is not a publish
        }
        Assert.Equal(1, await OutboxAsync($"tpost:{N(poster)}:{N(fan)}:"));
        Assert.Equal([fan], await NoticedAsync(NoticeKinds.FollowedPosted, post.TripId));
        await using var check = CreateContext();
        Assert.False((await check.Trips.AsNoTracking().SingleAsync(t => t.Id == post.TripId)).NotifyNearby);
    }

    // ------------------------------------------------------------------ caps (use cases 3, 25, 26, 30, 31)

    [IntegrationFact]
    // I-2 (owner 2026-11-28): no weekly notified-post cap; every new post tells followers and may queue nearby. Only the publish
    // cap (4 per rolling 7 days) stays, never refunded.
    public async Task Every_post_notifies_and_a_fifth_publish_is_refused_without_refunds()
    {
        var poster = await PersonAsync();
        var fan = await PersonAsync();
        await FollowAsync(fan, poster);
        var p1 = await CreateAsync(poster, Post());
        var p2 = await CreateAsync(poster, Post());
        var p3 = await CreateAsync(poster, Post(nearby: true));
        Assert.Equal(PostNotifyStates.Sent, p1.Notify!.Followers);
        Assert.Equal(PostNotifyStates.Sent, p2.Notify!.Followers);
        Assert.Equal((PostNotifyStates.Sent, PostNotifyStates.Queued), (p3.Notify!.Followers, p3.Notify.Nearby));
        Assert.Equal((0, 0), (p3.Notify.Used, p3.Notify.Limit));
        Assert.Null(p3.Notify.NextAllowedAt);
        Assert.Equal([fan], await NoticedAsync(NoticeKinds.FollowedPosted, p3.TripId));

        // Taking a post down never refunds a publish.
        await using (var ctx = CreateContext()) await UnpublishAfterLockAsync(ctx, poster, p1.TripId);
        var p4 = await CreateAsync(poster, Post());
        Assert.Equal(PostNotifyStates.Sent, p4.Notify!.Followers);
        await using (var ctx = CreateContext()) await UnpublishAfterLockAsync(ctx, poster, p2.TripId);
        await using (var ctx = CreateContext())
        {
            var fifth = await Posts(ctx).CreateAsync(poster, Post());
            Assert.Equal(SocialErrorTypes.PublishLimit, fifth.ErrorType);
            Assert.Equal(429, SocialErrorTypes.StatusOf(fifth.ErrorType!));
            var me = await Profiles(ctx).GetMeAsync(poster);
            Assert.Equal((0, 0, 4, 4), (me.Limits.NotifyPosts.Limit, me.Limits.NotifyPosts.Used, me.Limits.Publishes.Limit, me.Limits.Publishes.Used));
            Assert.NotNull(me.Limits.PublishNextAt);
        }
    }

    [IntegrationFact]
    public async Task A_new_account_is_not_capped_on_its_first_day()
    {
        var poster = await PersonAsync(oldAccount: false);
        var fan = await PersonAsync();
        await FollowAsync(fan, poster);
        Assert.Equal(PostNotifyStates.Sent, (await CreateAsync(poster, Post())).Notify!.Followers);
        var second = await CreateAsync(poster, Post());
        Assert.Equal((PostNotifyStates.Sent, 0), (second.Notify!.Followers, second.Notify.Limit));
    }

    // ------------------------------------------------------------------ nearby digest (use cases 2, 4, 5, 6, 7, 8, 11, 13, 14)

    [IntegrationFact]
    public async Task Morning_digest_tells_each_eligible_traveller_once_and_a_second_run_sends_nothing()
    {
        await ResetDigestStateAsync();
        var home = await DistrictAsync(29.38, 79.45);   // Nainital
        var far = await DistrictAsync(19.07, 72.87);    // Mumbai, far beyond 150 km
        var poster = await PersonAsync();
        var near = await PersonAsync();
        var woman = await PersonAsync(SocialGenders.Female);
        var off = await PersonAsync();
        var stale = await PersonAsync();
        var away = await PersonAsync();
        var fan = await PersonAsync();
        var blocked = await PersonAsync();
        foreach (var u in new[] { near, woman, off, fan, blocked }) await AreaAsync(u, home); // batch-loop
        await AreaAsync(stale, home, TimeSpan.FromDays(31));
        await AreaAsync(away, far);
        await using (var ctx = CreateContext())
        {
            Assert.True((await Profiles(ctx).UpdatePrivacyAsync(off, new UpdatePrivacyRequest { NotifyNearbyTrips = false })).Ok);
            ctx.UserBlocks.Add(new UserBlock { Id = Guid.NewGuid(), BlockerId = blocked, BlockedId = poster, CreatedAt = DateTime.UtcNow });
            await ctx.SaveChangesAsync();
        }
        await FollowAsync(fan, poster);

        var post = await CreateAsync(poster, Post(JoinGenders.Men, nearby: true));
        Assert.Equal((PostNotifyStates.Sent, PostNotifyStates.Queued), (post.Notify!.Followers, post.Notify.Nearby));
        Assert.Equal(0, await OutboxAsync($"nearby:{N(near)}:")); // nothing to strangers before 07:00

        await using (var ctx = CreateContext()) Assert.True(await Digest(ctx, DigestMorning).RunAsync(default) >= 1);
        Assert.Equal([near], await NoticedAsync(NoticeKinds.NearbyPosted, post.TripId));
        Assert.Equal(1, await OutboxAsync($"nearby:{N(near)}:{Day}"));
        Assert.Equal("Person 1 posted a trip near you", await BodyAsync($"nearby:{N(near)}:{Day}"));
        foreach (var u in new[] { woman, off, stale, away, fan, blocked, poster })
            Assert.Equal(0, await OutboxAsync($"nearby:{N(u)}:")); // batch-loop
        Assert.Equal(1, await OutboxAsync($"tpost:{N(poster)}:{N(fan)}:")); // the follower was told once, at publish

        await using (var ctx = CreateContext()) await Digest(ctx, DigestMorning).RunAsync(default);
        Assert.Equal([near], await NoticedAsync(NoticeKinds.NearbyPosted, post.TripId));
        Assert.Equal(1, await OutboxAsync($"nearby:{N(near)}:"));
        await using var check = CreateContext();
        Assert.NotNull((await check.Trips.AsNoTracking().SingleAsync(t => t.Id == post.TripId)).NearbyNotifiedAt);
        var own = await new PublicTripReader(check, TimeProvider.System).ByIdAsync(poster, post.TripId, default);
        Assert.True(own!.NotifyNearby);
        Assert.NotNull(own.NearbyNotifiedAt);
        Assert.Null((await new PublicTripReader(check, TimeProvider.System).ByIdAsync(near, post.TripId, default))!.NotifyNearby);
    }

    [IntegrationFact]
    public async Task Several_new_trips_merge_into_one_counted_push()
    {
        await ResetDigestStateAsync();
        var home = await DistrictAsync(29.38, 79.45);
        var reader = await PersonAsync();
        await AreaAsync(reader, home);
        var a = await CreateAsync(await PersonAsync(), Post(nearby: true));
        var b = await CreateAsync(await PersonAsync(), Post(nearby: true));
        await using (var ctx = CreateContext()) await Digest(ctx, DigestMorning).RunAsync(default);
        Assert.Contains(reader, await NoticedAsync(NoticeKinds.NearbyPosted, a.TripId));
        Assert.Contains(reader, await NoticedAsync(NoticeKinds.NearbyPosted, b.TripId));
        Assert.Equal(1, await OutboxAsync($"nearby:{N(reader)}:"));
        Assert.Equal("2 new trips near you", await BodyAsync($"nearby:{N(reader)}:{Day}"));
    }

    [IntegrationFact]
    public async Task Private_posters_late_runs_and_posts_taken_down_reach_no_stranger()
    {
        await ResetDigestStateAsync();
        var home = await DistrictAsync(29.38, 79.45);
        var reader = await PersonAsync();
        await AreaAsync(reader, home);
        var hidden = await CreateAsync(await PersonAsync(isPrivate: true), Post(nearby: true));
        Assert.Equal(PostNotifyStates.Private, hidden.Notify!.Nearby);
        var downPoster = await PersonAsync();
        var down = await CreateAsync(downPoster, Post(nearby: true));
        await using (var ctx = CreateContext()) await UnpublishAfterLockAsync(ctx, downPoster, down.TripId);
        var late = await CreateAsync(await PersonAsync(), Post(nearby: true));

        // 22:00 IST: past the catch-up window, nothing is sent and the post waits for the next morning.
        await using (var ctx = CreateContext())
            Assert.Equal(0, await Digest(ctx, IstTime.AtUtc(IstTime.TodayIst(DigestMorning), 22)).RunAsync(default));
        await using (var ctx = CreateContext())
            Assert.Null((await ctx.Trips.AsNoTracking().SingleAsync(t => t.Id == late.TripId)).NearbyNotifiedAt);

        await using (var ctx = CreateContext()) await Digest(ctx, DigestMorning).RunAsync(default);
        Assert.Empty(await NoticedAsync(NoticeKinds.NearbyPosted, hidden.TripId));
        Assert.Empty(await NoticedAsync(NoticeKinds.NearbyPosted, down.TripId));
        Assert.Contains(reader, await NoticedAsync(NoticeKinds.NearbyPosted, late.TripId));
    }

    // ------------------------------------------------------------------ private poster, other gender, full post (use cases 4, 8, 10)

    [IntegrationFact]
    public async Task A_private_poster_still_tells_followers_other_gender_hears_only_anyone_posts_and_a_full_post_reaches_no_stranger()
    {
        await ResetDigestStateAsync();
        var home = await DistrictAsync(29.38, 79.45);
        var reader = await PersonAsync();
        await AreaAsync(reader, home);

        var hidden = await PersonAsync(isPrivate: true);
        var fan = await PersonAsync(SocialGenders.Other);
        await FollowAsync(fan, hidden);
        await using (var ctx = CreateContext()) Assert.True((await Follows(ctx).AcceptRequestAsync(hidden, fan)).Ok);
        var anyone = await CreateAsync(hidden, Post(nearby: true));
        Assert.Equal((PostNotifyStates.Sent, PostNotifyStates.Private), (anyone.Notify!.Followers, anyone.Notify.Nearby));
        Assert.Equal([fan], await NoticedAsync(NoticeKinds.FollowedPosted, anyone.TripId));
        Assert.Equal(1, await OutboxAsync($"tpost:{N(hidden)}:{N(fan)}:"));
        var womenOnly = await CreateAsync(hidden, Post(JoinGenders.Women));
        Assert.Equal(PostNotifyStates.NoFollowers, womenOnly.Notify!.Followers);
        Assert.Empty(await NoticedAsync(NoticeKinds.FollowedPosted, womenOnly.TripId));

        var host = await PersonAsync();
        var full = await CreateAsync(host, Post(nearby: true));
        await using (var ctx = CreateContext())
        {
            var askers = await BuddySeed.UsersAsync(ctx, SocialLimits.PendingRequestsMax);
            foreach (var a in askers) // batch-loop: seed rows only
                ctx.Set<TripJoinRequest>().Add(new TripJoinRequest
                {
                    Id = Guid.NewGuid(), TripId = full.TripId, OwnerUserId = host, RequesterId = a, ClientRequestId = Guid.NewGuid(),
                    Tier = 2, Status = JoinRequestStatuses.Waiting, CreatedAt = DateTime.UtcNow,
                });
            await ctx.SaveChangesAsync();
        }
        var open = await CreateAsync(await PersonAsync(), Post(nearby: true));

        await using (var ctx = CreateContext()) await Digest(ctx, DigestMorning).RunAsync(default);
        Assert.Empty(await NoticedAsync(NoticeKinds.NearbyPosted, anyone.TripId));   // private poster: followers only
        Assert.Empty(await NoticedAsync(NoticeKinds.NearbyPosted, full.TripId));     // re-checked at send time: Full
        Assert.Contains(reader, await NoticedAsync(NoticeKinds.NearbyPosted, open.TripId));
    }

    // ------------------------------------------------------------------ requests (use cases 15, 16)

    [IntegrationFact]
    public async Task Fifty_waiting_requests_fill_a_post_and_a_decline_frees_a_slot()
    {
        var host = await PersonAsync();
        var post = await CreateAsync(host, Post());
        Guid first;
        await using (var ctx = CreateContext())
        {
            var askers = await BuddySeed.UsersAsync(ctx, SocialLimits.PendingRequestsMax);
            foreach (var a in askers) // batch-loop: seed rows only
                ctx.Set<TripJoinRequest>().Add(new TripJoinRequest
                {
                    Id = Guid.NewGuid(), TripId = post.TripId, OwnerUserId = host, RequesterId = a, ClientRequestId = Guid.NewGuid(),
                    Tier = 2, Status = JoinRequestStatuses.Waiting, CreatedAt = DateTime.UtcNow,
                });
            await ctx.SaveChangesAsync();
            first = await ctx.Set<TripJoinRequest>().Where(r => r.TripId == post.TripId).Select(r => r.Id).FirstAsync();
        }
        var late = await PersonAsync();
        await using (var ctx = CreateContext())
        {
            var refused = await Joins(ctx).SendAsync(late, post.TripId, new SendJoinRequest { ClientRequestId = Guid.NewGuid() });
            Assert.Equal(SocialErrorTypes.TripRequestsFull, refused.ErrorType);
            var seen = await new PublicTripReader(ctx, TimeProvider.System).ByIdAsync(late, post.TripId, default);
            Assert.True(seen!.RequestsFull);
            var feed = await new PublicTripReader(ctx, TimeProvider.System).FeedAsync(late, null, 50, null, null, null, null, default);
            Assert.All(feed.Items.Where(i => i.TripId == post.TripId), i => Assert.True(i.RequestsFull));
        }
        await using (var ctx = CreateContext()) Assert.True((await Joins(ctx).DeclineAsync(host, first)).Ok); // host actions unaffected
        await using (var ctx = CreateContext())
        {
            var sent = await Joins(ctx).SendAsync(late, post.TripId, new SendJoinRequest { ClientRequestId = Guid.NewGuid() });
            Assert.True(sent.Ok, sent.ErrorType);
        }
        Assert.Equal(1, await OutboxAsync($"reqfull:{N(post.TripId)}"));
    }

    [IntegrationFact]
    public async Task A_removed_member_cannot_ask_for_the_same_trip_again()
    {
        var host = await PersonAsync();
        var gone = await PersonAsync();
        var post = await CreateAsync(host, Post());
        await using (var ctx = CreateContext()) await BuddySeed.MembersAsync(ctx, post.TripId, host, [gone], TripMemberStatuses.Removed);
        await using var send = CreateContext();
        var refused = await Joins(send).SendAsync(gone, post.TripId, new SendJoinRequest { ClientRequestId = Guid.NewGuid() });
        Assert.Equal(SocialErrorTypes.CannotRejoin, refused.ErrorType);
        Assert.Equal(MyJoinStates.Removed, (await new PublicTripReader(send, TimeProvider.System).ByIdAsync(gone, post.TripId, default))!.MyJoin);
        Assert.Equal("You can no longer join this trip.", SocialErrorTypes.MessageOf(refused.ErrorType!));
    }

    // ------------------------------------------------------------------ area and settings (use cases 18, 21, 23)

    [IntegrationFact]
    public async Task Area_takes_a_known_district_only_and_is_written_at_most_every_6_hours()
    {
        var me = await PersonAsync();
        var home = await DistrictAsync(29.38, 79.45);
        await using var ctx = CreateContext();
        var profiles = Profiles(ctx);
        Assert.Equal(SocialErrorTypes.InvalidArea, (await profiles.SetAreaAsync(me, Guid.NewGuid())).ErrorType);
        var first = await profiles.SetAreaAsync(me, home);
        Assert.True(first.Ok, first.ErrorType);
        var second = await profiles.SetAreaAsync(me, home);
        Assert.Equal(first.Value!.UpdatedAt, second.Value!.UpdatedAt);
        var stored = await ctx.Set<UserProfile>().AsNoTracking().SingleAsync(p => p.UserId == me);
        Assert.Equal(home, stored.LastDistrictId);
        await using var noProfile = CreateContext();
        var stranger = (await BuddySeed.UsersAsync(noProfile, 1))[0];
        Assert.Equal(SocialErrorTypes.ProfileRequired, (await Profiles(noProfile).SetAreaAsync(stranger, home)).ErrorType);
    }

    [IntegrationFact]
    public async Task Both_switches_save_and_come_back_from_the_server()
    {
        var me = await PersonAsync();
        await using (var ctx = CreateContext())
        {
            var saved = await Profiles(ctx).UpdatePrivacyAsync(me, new UpdatePrivacyRequest { NotifyNearbyTrips = false, NotifyFollowedTrips = false });
            Assert.True(saved.Ok, saved.ErrorType);
        }
        await using var read = CreateContext();
        var profile = (await Profiles(read).GetMeAsync(me)).Profile!;
        Assert.Equal((false, false), (profile.NotifyNearbyTrips, profile.NotifyFollowedTrips));
    }

    // ------------------------------------------------------------------ safety review (review_safety.md S1-S4)

    [IntegrationFact]
    public async Task A_man_cannot_push_a_women_only_post_to_the_women_near_him()
    {
        await ResetDigestStateAsync();
        var home = await DistrictAsync(29.38, 79.45);
        var woman = await PersonAsync(SocialGenders.Female);
        await AreaAsync(woman, home);
        var man = await PersonAsync();
        var post = await CreateAsync(man, Post(JoinGenders.Women, nearby: true));
        Assert.Equal(PostNotifyStates.Gender, post.Notify!.Nearby);
        Assert.Equal(0, post.Notify.Used);                                  // a refused toggle never uses the weekly limit
        var womanPoster = await PersonAsync(SocialGenders.Female);
        var fine = await CreateAsync(womanPoster, Post(JoinGenders.Women, nearby: true));
        Assert.Equal(PostNotifyStates.Queued, fine.Notify!.Nearby);

        await using (var ctx = CreateContext()) await Digest(ctx, DigestMorning).RunAsync(default);
        Assert.Empty(await NoticedAsync(NoticeKinds.NearbyPosted, post.TripId));
        Assert.Equal([woman], await NoticedAsync(NoticeKinds.NearbyPosted, fine.TripId));
        await using var check = CreateContext();
        Assert.False((await check.Trips.AsNoTracking().SingleAsync(t => t.Id == post.TripId)).NotifyNearby);
    }

    [IntegrationFact]
    public async Task A_double_tap_on_publish_charges_one_publish_and_notifies_once()
    {
        var poster = await PersonAsync();
        var post = await CreateAsync(poster, Post());                       // no followers, toggle off: nothing told yet
        await using (var ctx = CreateContext()) await UnpublishAfterLockAsync(ctx, poster, post.TripId);
        await using var a = CreateContext();
        await using var b = CreateContext();
        var both = await Task.WhenAll(
            Posts(a).PutPostAsync(poster, post.TripId, Edit(nearby: true)),
            Posts(b).PutPostAsync(poster, post.TripId, Edit(nearby: true)));
        Assert.All(both, r => Assert.True(r.Ok, r.ErrorType));
        await using var check = CreateContext();
        var profile = await check.Set<UserProfile>().AsNoTracking().SingleAsync(p => p.UserId == poster);
        Assert.Equal(2, profile.PublishTimes.Length);                       // create + ONE re-publish
        Assert.True((await check.Trips.AsNoTracking().SingleAsync(t => t.Id == post.TripId)).NotifyNearby); // queued once
    }

    [IntegrationFact]
    public async Task A_nearby_post_still_pending_after_7_days_is_closed_without_a_send()
    {
        await ResetDigestStateAsync();
        var home = await DistrictAsync(29.38, 79.45);
        var reader = await PersonAsync();
        await AreaAsync(reader, home);
        var post = await CreateAsync(await PersonAsync(), Post(nearby: true));
        await using (var ctx = CreateContext())
            await ctx.Trips.Where(t => t.Id == post.TripId).ExecuteUpdateAsync(s => s
                .SetProperty(t => t.PublishedAt, (DateTime?)DateTime.UtcNow.AddDays(-9))
                .SetProperty(t => t.LastPublishedAt, (DateTime?)DateTime.UtcNow.AddDays(-9)));
        await using (var ctx = CreateContext()) await Digest(ctx, DigestMorning).RunAsync(default);
        Assert.Empty(await NoticedAsync(NoticeKinds.NearbyPosted, post.TripId));
        Assert.Equal(0, await OutboxAsync($"nearby:{N(reader)}:{Day}"));
        await using var check = CreateContext();
        Assert.NotNull((await check.Trips.AsNoTracking().SingleAsync(t => t.Id == post.TripId)).NearbyNotifiedAt);
    }

    // Verifier 2026-11-27 (use cases 2, 27): bump protection keeps the first PublishedAt on a re-publish, but the nearby queue counts
    // from the latest publish. A post first published silently (no followers, toggle off) and re-published late in its first week
    // with the toggle ON is told to the travellers near it the next morning, not closed as stale.
    [IntegrationFact]
    public async Task A_late_re_publish_with_the_toggle_is_sent_next_morning_not_closed_as_stale()
    {
        await ResetDigestStateAsync();
        var home = await DistrictAsync(29.38, 79.45);
        var reader = await PersonAsync();
        await AreaAsync(reader, home);
        var poster = await PersonAsync();
        var post = await CreateAsync(poster, Post());
        Assert.Equal(PostNotifyStates.NoFollowers, post.Notify!.Followers); // nobody told: no notified-post slot used
        // First publish just inside 7 days before tomorrow's run, so bump protection keeps it and the old rule saw it as stale.
        var firstPublish = PostNotifyRules.DigestCutoffUtc(IstTime.TodayIst(DigestMorning)) - PostNotifyRules.NearbyPendingMaxAge - TimeSpan.FromMinutes(1);
        await using (var ctx = CreateContext())
        {
            await ctx.Trips.Where(t => t.Id == post.TripId).ExecuteUpdateAsync(s => s.SetProperty(t => t.PublishedAt, (DateTime?)firstPublish));
            await UnpublishAfterLockAsync(ctx, poster, post.TripId);
        }
        await using (var ctx = CreateContext())
        {
            var again = await Posts(ctx).PutPostAsync(poster, post.TripId, Edit(nearby: true));
            Assert.True(again.Ok, again.ErrorType);
            Assert.Equal(PostNotifyStates.Queued, again.Value!.Notify!.Nearby);
            Assert.Equal(firstPublish, DateTime.SpecifyKind(again.Value.PublishedAt, DateTimeKind.Utc), TimeSpan.FromSeconds(1)); // feed place kept
        }
        await using (var ctx = CreateContext()) await Digest(ctx, DigestMorning).RunAsync(default);
        Assert.Equal([reader], await NoticedAsync(NoticeKinds.NearbyPosted, post.TripId));
        Assert.Equal(1, await OutboxAsync($"nearby:{N(reader)}:{Day}"));
    }
}
