using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
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

namespace RentNearBy.Tests.Social;

// Contract 1.3 / 1.4 / 2.3. Needs RENTNEARBY_TEST_DB; skipped without it.
public class FollowIntegrationTests(TestDatabase db) : IntegrationTestBase(db)
{
    private static FollowService Follows(ApplicationDbContext ctx)
        => new(ctx, new NoticeWriter(ctx), SocialTestSeams.AfterCommit(ctx), NullLogger<FollowService>.Instance);

    private static MyBuddyService Buddies(ApplicationDbContext ctx) => new(ctx, new RecordingLiveHints(), NullLogger<MyBuddyService>.Instance);

    private async Task<List<Guid>> PeopleAsync(int n, bool privateFirst = false)
    {
        await using var ctx = CreateContext();
        var ids = await BuddySeed.UsersAsync(ctx, n);
        for (var i = 0; i < ids.Count; i++) await SocialSeed.ProfileAsync(ctx, ids[i], isPrivate: privateFirst && i == 0);
        return ids;
    }

    private async Task<(int Followers, int Following)> CountersAsync(Guid user)
    {
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
            await Follows(ctx).UnfollowAsync(p[1], p[0]);
        }
        await using var check = CreateContext();
        Assert.Equal(0, await check.Set<BuddyLink>().CountAsync(b => b.UserId == p[0] && b.BuddyUserId == p[1]));
        Assert.Equal((0, 0), await CountersAsync(p[0]));
    }
}
