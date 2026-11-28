using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;
using RentNearBy.Core.Entities.Buddy;
using RentNearBy.Core.Entities.Social;
using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.Buddy.Be4;
using RentNearBy.Infrastructure.Services.Social;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Support.Buddy;
using RentNearBy.Tests.Support.Social;
using static RentNearBy.Infrastructure.Services.Social.SocialSql;

namespace RentNearBy.Tests.Social;

// Contract 1.3 / 1.4 / 2.3, Discover DESIGN 4.5 / 5.2 / 10.2. Needs RENTNEARBY_TEST_DB; skipped without it.
public class FollowIntegrationTests(TestDatabase db) : IntegrationTestBase(db)
{
    private static FollowService Follows(ApplicationDbContext ctx)
        => new(ctx, new NoticeWriter(ctx), SocialTestSeams.AfterCommit(ctx), NullLogger<FollowService>.Instance);

    private static FollowEffectsWork Effects(ApplicationDbContext ctx)
        => new(ctx, new NoticeWriter(ctx), SocialTestSeams.AfterCommit(ctx), NullLogger<FollowEffectsWork>.Instance);

    private static MyBuddyService Buddies(ApplicationDbContext ctx) => new(ctx, new RecordingLiveHints(), NullLogger<MyBuddyService>.Instance);

    private async Task<List<Guid>> PeopleAsync(int n, bool privateFirst = false)
    {
        await using var ctx = CreateContext();
        var ids = await BuddySeed.UsersAsync(ctx, n);
        for (var i = 0; i < ids.Count; i++) await SocialSeed.ProfileAsync(ctx, ids[i], isPrivate: privateFirst && i == 0);
        return ids;
    }

    // Counters come from FollowEffectsJob (DESIGN 5.2): one job cycle first, then they must equal a recount.
    private async Task<(int Followers, int Following)> CountersAsync(Guid user)
    {
        await using (var job = CreateContext()) await Effects(job).RunOnceAsync(CancellationToken.None);
        await using var ctx = CreateContext();
        var p = await ctx.Set<UserProfile>().AsNoTracking().SingleAsync(x => x.UserId == user);
        var followers = await ctx.Set<Follow>().CountAsync(f => f.FolloweeId == user && f.Status == FollowStatuses.Accepted);
        var following = await ctx.Set<Follow>().CountAsync(f => f.FollowerId == user && f.Status == FollowStatuses.Accepted);
        Assert.Equal((followers, following), (p.FollowerCount, p.FollowingCount));
        return (p.FollowerCount, p.FollowingCount);
    }

    [IntegrationFact]
    public async Task Public_follow_is_accepted_and_counters_equal_a_recount()
    {
        var p = await PeopleAsync(2);
        await using (var ctx = CreateContext())
        {
            var r = await Follows(ctx).FollowAsync(p[1], p[0]);
            Assert.Equal(FollowStates.Following, r.Value!.State);
            Assert.Equal(FollowStates.Following, (await Follows(ctx).FollowAsync(p[1], p[0])).Value!.State);
        }
        Assert.Equal((1, 0), await CountersAsync(p[0]));
        Assert.Equal((0, 1), await CountersAsync(p[1]));
    }

    [IntegrationFact]
    public async Task Private_follow_waits_then_accept_and_decline()
    {
        var p = await PeopleAsync(3, privateFirst: true);
        await using (var ctx = CreateContext())
        {
            Assert.Equal(FollowStates.Requested, (await Follows(ctx).FollowAsync(p[1], p[0])).Value!.State);
            Assert.Equal(FollowStates.Requested, (await Follows(ctx).FollowAsync(p[2], p[0])).Value!.State);
            Assert.Equal(FollowStates.Accepted, (await Follows(ctx).AcceptRequestAsync(p[0], p[1])).Value!.State);
            await Follows(ctx).DeclineRequestAsync(p[0], p[2]);
            Assert.Equal(SocialErrorTypes.RequestGone, (await Follows(ctx).AcceptRequestAsync(p[0], p[2])).ErrorType);
        }
        Assert.Equal((1, 0), await CountersAsync(p[0]));
    }

    [IntegrationFact]
    public async Task My_buddies_need_a_follower_and_unfollow_removes_the_buddy_row()
    {
        var p = await PeopleAsync(2);
        await using (var ctx = CreateContext())
        {
            Assert.Equal(SocialErrorTypes.NotFollower, (await Buddies(ctx).AddAsync(p[0], p[1])).ErrorType);
            await Follows(ctx).FollowAsync(p[1], p[0]);
            Assert.True((await Buddies(ctx).AddAsync(p[0], p[1])).Value!.IsBuddy);
        }
        await using (var ctx = CreateContext())
        {
            Assert.Equal(1, await ctx.Set<BuddyLink>().CountAsync(b => b.UserId == p[0] && b.BuddyUserId == p[1]));
            Assert.Equal(0, await ctx.Set<BuddyLink>().CountAsync(b => b.UserId == p[1] && b.BuddyUserId == p[0]));
            Assert.True(await Follows(ctx).UnfollowAsync(p[1], p[0]));
            Assert.False(await Follows(ctx).UnfollowAsync(p[1], p[0]));
        }
        await using var check = CreateContext();
        Assert.Equal(0, await check.Set<BuddyLink>().CountAsync(b => b.UserId == p[0] && b.BuddyUserId == p[1]));
        Assert.Equal((0, 0), await CountersAsync(p[0]));
    }

    // ------------------------------------------------------------------ follow guard, DB layer

    private async Task SetSendTimesAsync(Guid user, DateTime[] times)
    {
        await using var ctx = CreateContext();
        await ctx.Database.ExecuteSqlRawAsync("""UPDATE "UserProfiles" SET "FollowSendTimes" = @t WHERE "UserId" = @u""",
            [Uuid("u", user), new NpgsqlParameter("t", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { Value = times }]);
    }

    private static DateTime[] FullWeek()
    {
        var now = DateTime.UtcNow;
        return [.. Enumerable.Range(1, FollowGuardRules.WeekMax).Select(i => now.AddHours(-26 * i))];
    }

    private async Task<UserProfile> ProfileAsync(Guid user)
    {
        await using var ctx = CreateContext();
        return await ctx.Set<UserProfile>().AsNoTracking().SingleAsync(x => x.UserId == user);
    }

    private async Task<FollowWriteOutcome> WriteAsync(Guid me, Guid him, bool mustBeExempt = false)
    {
        await using var ctx = CreateContext();
        return await Follows(ctx).FollowAsync(me, him, mustBeExempt, CancellationToken.None);
    }

    [IntegrationFact]
    public async Task A_new_follow_is_one_row_with_effects_pending_and_accept_sets_effects()
    {
        var p = await PeopleAsync(3, privateFirst: true);
        await WriteAsync(p[2], p[1]);
        await WriteAsync(p[1], p[0]);
        await using (var ctx = CreateContext())
        {
            var accepted = await ctx.Set<Follow>().AsNoTracking().SingleAsync(f => f.FollowerId == p[2] && f.FolloweeId == p[1]);
            Assert.Null(accepted.EffectsAt);
            Assert.Equal(accepted.CreatedAt, accepted.AcceptedAt);
            var requested = await ctx.Set<Follow>().AsNoTracking().SingleAsync(f => f.FollowerId == p[1] && f.FolloweeId == p[0]);
            Assert.Null(requested.EffectsAt);
            Assert.Null(requested.AcceptedAt);
            var p0 = $"fol:{p[0]:N}";
            var p1 = $"fol:{p[1]:N}";
            Assert.Equal(0, await ctx.OutboxMessages.CountAsync(o => o.Key.StartsWith(p0) || o.Key.StartsWith(p1)));
            await Follows(ctx).AcceptRequestAsync(p[0], p[1]);
        }
        await using var check = CreateContext();
        Assert.NotNull((await check.Set<Follow>().AsNoTracking().SingleAsync(f => f.FollowerId == p[1] && f.FolloweeId == p[0])).EffectsAt);
    }

    [IntegrationFact]
    public async Task Redis_down_twenty_parallel_follows_count_exactly_the_limit()
    {
        var p = await PeopleAsync(21);
        var results = await Task.WhenAll(Enumerable.Range(1, 20).Select(i => WriteAsync(p[0], p[i])));
        var limit = FollowGuardRules.DayMax > 0 ? Math.Min(FollowGuardRules.DayMax, FollowGuardRules.WeekMax) : FollowGuardRules.WeekMax;
        Assert.Equal(limit, results.Count(r => r.Result.Ok));
        Assert.Equal(limit, results.Count(r => r.Guard.Counted));
        Assert.All(results.Where(r => !r.Result.Ok), r => Assert.Equal(SocialErrorTypes.TryLater, r.Result.ErrorType));
        Assert.Equal(limit, (await ProfileAsync(p[0])).FollowSendTimes.Length);
        await using var ctx = CreateContext();
        Assert.Equal(limit, await ctx.Set<Follow>().CountAsync(f => f.FollowerId == p[0]));
    }

    [IntegrationFact]
    public async Task A_redis_over_count_with_room_in_the_db_passes_heals_and_is_not_wasted()
    {
        var p = await PeopleAsync(2);
        var r = await WriteAsync(p[0], p[1], mustBeExempt: true);
        Assert.True(r.Result.Ok);
        Assert.Equal(new FollowGuardOutcome(true, true, false), r.Guard);
        Assert.Single((await ProfileAsync(p[0])).FollowSendTimes);
    }

    [IntegrationFact]
    public async Task A_redis_under_count_is_refused_by_the_db_which_drops_the_marker()
    {
        var p = await PeopleAsync(2);
        await SetSendTimesAsync(p[0], FullWeek());
        var r = await WriteAsync(p[0], p[1]);
        Assert.Equal(SocialErrorTypes.TryLater, r.Result.ErrorType);
        Assert.Equal(new FollowGuardOutcome(false, true, true), r.Guard);
        await using var ctx = CreateContext();
        Assert.Equal(0, await ctx.Set<Follow>().CountAsync(f => f.FollowerId == p[0]));
    }

    [IntegrationFact]
    public async Task A_follow_back_is_allowed_while_the_week_is_full_and_not_counted()
    {
        var p = await PeopleAsync(2);
        await WriteAsync(p[1], p[0]);
        await SetSendTimesAsync(p[0], FullWeek());
        var r = await WriteAsync(p[0], p[1], mustBeExempt: true);
        Assert.Equal(FollowStates.Following, r.Result.Value!.State);
        Assert.Equal(FollowGuardOutcome.Nothing, r.Guard);
        Assert.Equal(FollowGuardRules.WeekMax, (await ProfileAsync(p[0])).FollowSendTimes.Length);
    }

    [IntegrationFact]
    public async Task Ten_parallel_duplicates_write_one_row_count_once_and_waste_the_rest()
    {
        var p = await PeopleAsync(2);
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => WriteAsync(p[0], p[1])));
        Assert.All(results, r => Assert.Equal(FollowStates.Following, r.Result.Value!.State));
        Assert.Equal(1, results.Count(r => r.Guard.Counted));
        Assert.Equal(9, results.Count(r => r.Guard.Wasted));
        Assert.Single((await ProfileAsync(p[0])).FollowSendTimes);
        await using var ctx = CreateContext();
        Assert.Equal(1, await ctx.Set<Follow>().CountAsync(f => f.FollowerId == p[0]));
    }

    [IntegrationFact]
    public async Task A_block_in_the_db_refuses_even_when_redis_allows()
    {
        var p = await PeopleAsync(2);
        await using (var ctx = CreateContext())
            await ctx.Database.ExecuteSqlRawAsync(
                """UPDATE "UserProfiles" SET "FollowBlockUntil" = now() + interval '1 hour' WHERE "UserId" = @u""", [Uuid("u", p[0])]);
        var r = await WriteAsync(p[0], p[1]);
        Assert.Equal(SocialErrorTypes.TryLater, r.Result.ErrorType);
        Assert.Equal(new FollowGuardOutcome(false, true, true), r.Guard);
    }

    [IntegrationFact]
    public async Task Redis_down_the_eleventh_attempt_in_a_minute_escalates_in_the_db()
    {
        var p = await PeopleAsync(2);
        var guard = new FollowGuard(null, new MemoryCache(new MemoryCacheOptions()), NullLogger<FollowGuard>.Instance);
        await using (var ctx = CreateContext())
        {
            for (var i = 0; i < FollowGuardRules.BurstMax; i++)
            {
                var ticket = await guard.TryAsync(ctx, p[0], p[1], CancellationToken.None);
                Assert.NotNull(ticket);
                Assert.False(ticket!.ViaRedis);
                Assert.False(ticket.MustBeExempt);
            }
            Assert.Null(await guard.TryAsync(ctx, p[0], p[1], CancellationToken.None));
        }
        var me = await ProfileAsync(p[0]);
        Assert.Equal((short)1, me.FollowBlockLevel);
        Assert.True(me.FollowBlockUntil > DateTime.UtcNow.AddHours(23));
        Assert.Equal(SocialErrorTypes.TryLater, (await WriteAsync(p[0], p[1])).Result.ErrorType);
    }
}
