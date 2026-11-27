using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RentNearBy.Core.DTOs.Social;
using RentNearBy.Core.Entities.Social;
using RentNearBy.Core.Interfaces.Social;
using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Repositories;
using RentNearBy.Infrastructure.Services.Buddy.Be2;
using RentNearBy.Infrastructure.Services.Buddy.Be4;
using RentNearBy.Infrastructure.Services.Social;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Support.Buddy;
using RentNearBy.Tests.Support.Social;

namespace RentNearBy.Tests.Social;

// PLAN_STATES_SOCIAL 4: every Seeker key through before / during / after / resubscribe on a real database. The plan pipeline is
// stood in for by a limits seam that switches between the Free and Plus numbers (grant, expiry, hold and refund all reach the
// reconcile through PlanSlotService with these numbers). Needs RENTNEARBY_TEST_DB (PostGIS); skipped without it.
public class SeekerPlanIntegrationTests(TestDatabase db) : IntegrationTestBase(db)
{
    private static readonly SeekerLimits Free = new(false, true, 1, 1, 1, 3, false, false, false);
    private static readonly SeekerLimits Plus = new(true, false, 6, 3, 5, 10, true, true, true);

    private sealed class SwitchLimits(SeekerLimits start) : ISeekerLimits
    {
        public SeekerLimits Current { get; set; } = start;

        public Task<SeekerLimits> GetAsync(Guid userId, CancellationToken ct = default) => Task.FromResult(Current);
    }

    private static SeekerPlanReconcile Reconcile(ApplicationDbContext ctx, ISeekerLimits limits, RecordingLiveHints? hints = null)
        => new(ctx, limits, hints ?? new RecordingLiveHints(), NullLogger<SeekerPlanReconcile>.Instance);

    private static PublicTripService Posts(ApplicationDbContext ctx, ISeekerLimits limits) =>
        new(ctx, new UnitOfWork(ctx), new FakeCityCatalog(), limits, new PublicTripReader(ctx, TimeProvider.System),
            SocialTestSeams.AfterCommit(ctx), new RentNearBy.Infrastructure.Services.Buddy.Be4.NoticeWriter(ctx), TimeProvider.System,
            NullLogger<PublicTripService>.Instance);

    private static JoinRequestService Joins(ApplicationDbContext ctx, ISeekerLimits limits)
    {
        var ac = SocialTestSeams.AfterCommit(ctx);
        return new JoinRequestService(ctx, limits, new NoticeWriter(ctx), new RecordingLiveHints(), SocialTestSeams.GroupChat(ctx, ac),
            new PublicTripReader(ctx, TimeProvider.System), ac, TimeProvider.System, NullLogger<JoinRequestService>.Instance);
    }

    private static CreatePublicTripRequest Post(string joinGender = JoinGenders.Anyone) => new()
    {
        ClientTripId = Guid.NewGuid(), CityKeys = [FakeCityCatalog.Nainital], WhenKind = SocialRules.WhenKindWithin,
        WithinMonths = 2, Days = 3, JoinGender = joinGender, Seats = 2, Text = "Easy hill walks, looking for one more.",
    };

    // The plan pipeline's shape: a transaction with the owner's Users row lock, then the reconcile.
    private static async Task<SeekerPlanEffect> ApplyAsync(ApplicationDbContext ctx, ISeekerLimits limits, Guid user)
    {
        await using var tx = await ctx.Database.BeginTransactionAsync();
        await ctx.Database.ExecuteSqlRawAsync("""SELECT 1 FROM "Users" WHERE "Id" = {0} FOR UPDATE""", user);
        var effect = await Reconcile(ctx, limits).ApplyAsync(user);
        await tx.CommitAsync();
        return effect;
    }

    private async Task<Guid> SeekerAsync(string gender = SocialGenders.Male)
    {
        await using var ctx = CreateContext();
        var id = (await BuddySeed.UsersAsync(ctx, 1))[0];
        await SocialSeed.ProfileAsync(ctx, id, gender);
        return id;
    }

    // n plain trips, oldest first, all made after the owner's social onboarding (not grandfathered).
    private async Task<List<Guid>> TripsAsync(Guid owner, int n, TimeSpan? beforeProfile = null)
    {
        await using var ctx = CreateContext();
        var completed = await ctx.Set<UserProfile>().Where(p => p.UserId == owner).Select(p => p.CompletedAt).SingleAsync();
        var ids = new List<Guid>();
        for (var i = 0; i < n; i++)
        {
            var id = await BuddySeed.TripAsync(ctx, owner);
            var at = beforeProfile is { } back ? completed - back + TimeSpan.FromMinutes(i) : completed + TimeSpan.FromMinutes(i + 1);
            await ctx.Trips.Where(t => t.Id == id).ExecuteUpdateAsync(s => s.SetProperty(t => t.CreatedAt, at));
            ids.Add(id);
        }
        return ids;
    }

    private async Task<Dictionary<Guid, (bool Inactive, bool Public, bool Paused)>> StateAsync(Guid owner)
    {
        await using var ctx = CreateContext();
        return await ctx.Trips.AsNoTracking().Where(t => t.OwnerUserId == owner)
            .ToDictionaryAsync(t => t.Id, t => (t.InactiveByPlan, t.IsPublic, t.PausedByPlan));
    }

    [IntegrationFact]
    public async Task Active_trips_after_expiry_keep_the_last_added_and_resubscribe_restores_them()
    {
        var owner = await SeekerAsync();
        var trips = await TripsAsync(owner, 3);

        await using (var ctx = CreateContext())
        {
            var during = await ApplyAsync(ctx, new SwitchLimits(Plus), owner);
            Assert.False(during.Changed);
            var after = await ApplyAsync(ctx, new SwitchLimits(Free), owner);
            Assert.Equal(2, after.Lost);
            Assert.False((await ApplyAsync(ctx, new SwitchLimits(Free), owner)).Changed);
            Assert.Equal(1, (await TripSlots.ReadAsync(ctx, owner, 1)).Count);
            Assert.Equal(new SeekerPlanCounts(1, 2), await SeekerPlanReconcile.CountsAsync(ctx, owner));
        }
        var state = await StateAsync(owner);
        Assert.False(state[trips[2]].Inactive);
        Assert.True(state[trips[0]].Inactive && state[trips[1]].Inactive);
        Assert.Equal(3, state.Count);

        await using (var ctx = CreateContext())
            Assert.True((await ApplyAsync(ctx, new SwitchLimits(Plus), owner)).Changed);
        Assert.All((await StateAsync(owner)).Values, s => Assert.False(s.Inactive));
    }

    [IntegrationFact]
    public async Task Public_trips_after_expiry_pause_older_posts_expire_waiting_requests_and_come_back_with_plus()
    {
        var owner = await SeekerAsync();
        var asker = await SeekerAsync();
        var plus = new SwitchLimits(Plus);
        var posted = new List<Guid>();
        await using (var ctx = CreateContext())
        {
            for (var i = 0; i < 3; i++)
            {
                var r = await Posts(ctx, plus).CreateAsync(owner, Post());
                Assert.True(r.Ok, r.ErrorType);
                posted.Add(r.Value!.TripId);
            }
            Assert.Equal(SocialErrorTypes.PublicTripLimit, (await Posts(ctx, plus).CreateAsync(owner, Post())).ErrorType);
            Assert.True((await Joins(ctx, plus).SendAsync(asker, posted[0], new SendJoinRequest { ClientRequestId = Guid.NewGuid() })).Ok);
        }

        await using (var ctx = CreateContext())
            await ApplyAsync(ctx, new SwitchLimits(Free), owner);
        var after = await StateAsync(owner);
        Assert.True(after[posted[2]].Public);
        // I-3: a post never goes inactive by the active-trip limit; the public cap pauses the older ones.
        Assert.Equal((false, false, true), after[posted[0]]);
        Assert.Equal((false, false, true), after[posted[1]]);
        await using (var check = CreateContext())
            Assert.Equal(JoinRequestStatuses.Expired, await check.Set<TripJoinRequest>().Where(r => r.TripId == posted[0]).Select(r => r.Status).SingleAsync());

        await using (var ctx = CreateContext())
            await ApplyAsync(ctx, plus, owner);
        var back = await StateAsync(owner);
        Assert.All(posted, id => Assert.Equal((false, true, false), back[id]));
        await using var still = CreateContext();
        Assert.Equal(JoinRequestStatuses.Expired, await still.Set<TripJoinRequest>().Where(r => r.TripId == posted[0]).Select(r => r.Status).SingleAsync());
    }

    [IntegrationFact]
    public async Task An_inactive_trip_refuses_a_post_edit_with_a_typed_error()
    {
        var owner = await SeekerAsync();
        var trips = await TripsAsync(owner, 2);
        await using var ctx = CreateContext();
        await ApplyAsync(ctx, new SwitchLimits(Free), owner);
        var edit = new UpdatePublicPostRequest
        {
            WhenKind = SocialRules.WhenKindWithin, WithinMonths = 2, Days = 3, JoinGender = JoinGenders.Anyone, Seats = 2, Text = "Hills in spring.",
        };
        var refused = await Posts(ctx, new SwitchLimits(Free)).PutPostAsync(owner, trips[0], edit);
        Assert.Equal(SocialErrorTypes.TripInactiveByPlan, refused.ErrorType);
        Assert.False((await StateAsync(owner))[trips[1]].Inactive);
    }

    [IntegrationFact]
    public async Task A_same_gender_post_keeps_its_restriction_after_expiry_and_a_new_one_needs_plus_again()
    {
        var woman = await SeekerAsync(SocialGenders.Female);
        Guid tripId;
        await using (var ctx = CreateContext())
        {
            var made = await Posts(ctx, new SwitchLimits(Plus)).CreateAsync(woman, Post(JoinGenders.Women));
            Assert.True(made.Ok, made.ErrorType);
            tripId = made.Value!.TripId;
            await ApplyAsync(ctx, new SwitchLimits(Free), woman);
        }
        await using var check = CreateContext();
        var trip = await check.Trips.AsNoTracking().SingleAsync(t => t.Id == tripId);
        Assert.Equal((JoinGenders.Women, true, false), (trip.JoinGender, trip.IsPublic, trip.InactiveByPlan));
        Assert.Equal(SocialErrorTypes.PlusRequired, (await Posts(check, new SwitchLimits(Free)).CreateAsync(woman, Post(JoinGenders.Women))).ErrorType);
    }

    [IntegrationFact]
    public async Task Grandfathered_trips_stay_active_after_expiry()
    {
        var owner = await SeekerAsync();
        await TripsAsync(owner, 3, beforeProfile: TimeSpan.FromDays(30));
        await using (var ctx = CreateContext())
            Assert.False((await ApplyAsync(ctx, new SwitchLimits(Free), owner)).Changed);
        Assert.All((await StateAsync(owner)).Values, s => Assert.False(s.Inactive));
    }

    [IntegrationFact]
    public async Task Join_requests_spent_on_plus_still_count_this_month_after_expiry()
    {
        var asker = await SeekerAsync();
        var plus = new SwitchLimits(Plus);
        var posts = new List<Guid>();
        await using (var ctx = CreateContext())
        {
            for (var i = 0; i < 3; i++)
            {
                var owner = await SeekerAsync();
                posts.Add((await Posts(ctx, plus).CreateAsync(owner, Post())).Value!.TripId);
            }
            Assert.True((await Joins(ctx, plus).SendAsync(asker, posts[0], new SendJoinRequest { ClientRequestId = Guid.NewGuid() })).Ok);
            Assert.True((await Joins(ctx, plus).SendAsync(asker, posts[1], new SendJoinRequest { ClientRequestId = Guid.NewGuid() })).Ok);
        }
        await using var after = CreateContext();
        var refused = await Joins(after, new SwitchLimits(Free)).SendAsync(asker, posts[2], new SendJoinRequest { ClientRequestId = Guid.NewGuid() });
        Assert.Equal(SocialErrorTypes.JoinRequestLimit, refused.ErrorType);
    }

    [IntegrationFact]
    public async Task Lazy_read_fixes_a_drift_and_two_at_once_end_in_one_state()
    {
        var owner = await SeekerAsync();
        await TripsAsync(owner, 4);
        var hints = new RecordingLiveHints();
        await using (var a = CreateContext())
        await using (var b = CreateContext())
        {
            var free = new SwitchLimits(Free);
            await Task.WhenAll(Reconcile(a, free, hints).EnsureAsync(owner), Reconcile(b, free, hints).EnsureAsync(owner));
        }
        var state = await StateAsync(owner);
        Assert.Equal(1, state.Values.Count(s => !s.Inactive));
        Assert.Contains(hints.SocialChanged, h => h.Scope == SocialLiveScopes.Limits);
    }

    // I-3 (owner 2026-11-28): a post does not take the trip place. Free: 1 trip + 1 post at once; Plus ends with 2 trips and
    // 2 posts: the newest trip and the newest post stay, nothing else is lost; a resubscribe restores all.
    [IntegrationFact]
    public async Task A_post_and_a_trip_live_together_on_free_and_survive_expiry_and_resubscribe()
    {
        var owner = await SeekerAsync();
        var free = new SwitchLimits(Free);
        var trips = await TripsAsync(owner, 1);
        Guid post;
        await using (var ctx = CreateContext())
        {
            var made = await Posts(ctx, free).CreateAsync(owner, Post());
            Assert.True(made.Ok, made.ErrorType);
            post = made.Value!.TripId;
            Assert.Equal(1, (await TripSlots.ReadAsync(ctx, owner, 1)).Count);          // the post holds no slot
            Assert.False((await ApplyAsync(ctx, free, owner)).Changed);
        }

        var plus = new SwitchLimits(Plus);
        trips.AddRange(await TripsAsync(owner, 1));
        Guid post2;
        await using (var ctx = CreateContext())
        {
            await ctx.Trips.Where(t => t.Id == trips[1]).ExecuteUpdateAsync(x => x.SetProperty(t => t.CreatedAt, t => t.CreatedAt.AddMinutes(10)));
            var made = await Posts(ctx, plus).CreateAsync(owner, Post());
            Assert.True(made.Ok, made.ErrorType);
            post2 = made.Value!.TripId;
            Assert.False((await ApplyAsync(ctx, plus, owner)).Changed);
            var ended = await ApplyAsync(ctx, free, owner);
            Assert.Equal(2, ended.Lost);
            Assert.Equal(1, (await TripSlots.ReadAsync(ctx, owner, 1)).Count);
        }
        var after = await StateAsync(owner);
        Assert.True(after[trips[0]].Inactive);
        Assert.False(after[trips[1]].Inactive);
        Assert.Equal((false, false, true), after[post]);
        Assert.Equal((false, true, false), after[post2]);

        await using (var ctx = CreateContext())
            Assert.True((await ApplyAsync(ctx, plus, owner)).Changed);
        Assert.All((await StateAsync(owner)).Values, x => Assert.False(x.Inactive || x.Paused));
    }

    // REFUND (PLAN_STATES_SOCIAL 4): a refunded Plus ends at once and takes the rows back exactly like an expiry; buying again restores.
    [IntegrationFact]
    public async Task Refund_mid_plus_takes_the_rows_back_like_an_expiry_and_a_new_purchase_restores_them()
    {
        var owner = await SeekerAsync();
        var plus = new SwitchLimits(Plus);
        var posted = new List<Guid>();
        await using (var ctx = CreateContext())
            for (var i = 0; i < 2; i++) // batch-loop: two posts made during Plus
                posted.Add((await Posts(ctx, plus).CreateAsync(owner, Post())).Value!.TripId);

        await using (var ctx = CreateContext())
        {
            var refunded = await ApplyAsync(ctx, new SwitchLimits(Free), owner);
            Assert.True(refunded.Changed);
            Assert.Equal(new SeekerPlanCounts(1, 1), await SeekerPlanReconcile.CountsAsync(ctx, owner));
        }
        var after = await StateAsync(owner);
        Assert.Equal(0, after.Values.Count(s => s.Inactive));
        Assert.Equal((false, false, true), after[posted[0]]);
        Assert.True(after[posted[1]].Public);

        await using (var ctx = CreateContext())
            Assert.True((await ApplyAsync(ctx, plus, owner)).Changed);
        Assert.All((await StateAsync(owner)).Values, s => Assert.False(s.Inactive || s.Paused));
    }

    // Concurrency per key: two creates at the limit at the same moment end with exactly one write.
    [IntegrationFact]
    public async Task Two_public_trip_creates_at_the_free_limit_write_one()
    {
        var owner = await SeekerAsync();
        var free = new SwitchLimits(Free);
        await using var a = CreateContext();
        await using var b = CreateContext();
        var results = await Task.WhenAll(Posts(a, free).CreateAsync(owner, Post()), Posts(b, free).CreateAsync(owner, Post()));
        Assert.Single(results, r => r.Ok);
        Assert.Single(results, r => r.ErrorType == SocialErrorTypes.PublicTripLimit);
        await using var check = CreateContext();
        Assert.Equal(1, await check.Trips.CountAsync(t => t.OwnerUserId == owner));
    }

    [IntegrationFact]
    public async Task Two_join_requests_at_the_free_monthly_limit_write_one()
    {
        var asker = await SeekerAsync();
        var plus = new SwitchLimits(Plus);
        var posts = new List<Guid>();
        await using (var ctx = CreateContext())
            for (var i = 0; i < 2; i++) // batch-loop: two owners, one post each
                posts.Add((await Posts(ctx, plus).CreateAsync(await SeekerAsync(), Post())).Value!.TripId);
        var free = new SwitchLimits(Free);
        await using var a = CreateContext();
        await using var b = CreateContext();
        var results = await Task.WhenAll(
            Joins(a, free).SendAsync(asker, posts[0], new SendJoinRequest { ClientRequestId = Guid.NewGuid() }),
            Joins(b, free).SendAsync(asker, posts[1], new SendJoinRequest { ClientRequestId = Guid.NewGuid() }));
        Assert.Single(results, r => r.Ok);
        Assert.Single(results, r => r.ErrorType == SocialErrorTypes.JoinRequestLimit);
        await using var check = CreateContext();
        Assert.Equal(1, await check.Set<TripJoinRequest>().CountAsync(r => r.RequesterId == asker));
    }

    [IntegrationFact]
    public async Task Two_new_chats_at_the_free_monthly_limit_write_one()
    {
        var me = await SeekerAsync();
        var people = new List<Guid>();
        for (var i = 0; i < 4; i++) people.Add(await SeekerAsync()); // batch-loop: four strangers
        var free = new SwitchLimits(Free);
        Task<RentNearBy.Core.Models.Buddy.BuddyResult<StartedChatDto>> Start(ApplicationDbContext ctx, Guid him)
            => new SocialChatService(ctx, free, new NoticeWriter(ctx), SocialTestSeams.AfterCommit(ctx), TimeProvider.System,
                NullLogger<SocialChatService>.Instance).StartAsync(me, him, Guid.NewGuid(), "Hi, same trip?");
        await using (var ctx = CreateContext())
            for (var i = 0; i < Free.NewChatsPerMonth - 1; i++) // batch-loop: up to one below the cap
                Assert.True((await Start(ctx, people[i])).Ok);
        await using var a = CreateContext();
        await using var b = CreateContext();
        var results = await Task.WhenAll(Start(a, people[^2]), Start(b, people[^1]));
        Assert.Single(results, r => r.Ok);
        Assert.Single(results, r => r.ErrorType == SocialErrorTypes.NewChatLimit);
    }

    // Month rollover: the monthly counters count from the 1st of the IST month; last month's requests never count.
    [IntegrationFact]
    public async Task Last_month_join_requests_do_not_count_after_the_month_rolls_over()
    {
        var asker = await SeekerAsync();
        var plus = new SwitchLimits(Plus);
        var posts = new List<Guid>();
        await using (var ctx = CreateContext())
        {
            for (var i = 0; i < 2; i++) // batch-loop: two owners, one post each
                posts.Add((await Posts(ctx, plus).CreateAsync(await SeekerAsync(), Post())).Value!.TripId);
            Assert.True((await Joins(ctx, new SwitchLimits(Free)).SendAsync(asker, posts[0], new SendJoinRequest { ClientRequestId = Guid.NewGuid() })).Ok);
            var lastMonth = SocialMonth.StartUtc(DateTime.UtcNow).AddDays(-1);
            await ctx.Set<TripJoinRequest>().Where(r => r.RequesterId == asker).ExecuteUpdateAsync(s => s.SetProperty(r => r.CreatedAt, lastMonth));
        }
        await using var next = CreateContext();
        var fresh = await Joins(next, new SwitchLimits(Free)).SendAsync(asker, posts[1], new SendJoinRequest { ClientRequestId = Guid.NewGuid() });
        Assert.True(fresh.Ok, fresh.ErrorType);
    }
}
