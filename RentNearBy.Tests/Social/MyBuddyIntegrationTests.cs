using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RentNearBy.Core.Entities.Buddy;
using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.Buddy.Be1;
using RentNearBy.Infrastructure.Services.Buddy.Be4;
using RentNearBy.Infrastructure.Services.Social;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Support.Buddy;
using RentNearBy.Tests.Support.Social;

namespace RentNearBy.Tests.Social;

// Contract 1.4 / D3: My Buddies is a one-way list picked from my followers; remove takes only my row.
// Needs RENTNEARBY_TEST_DB; skipped without it.
public class MyBuddyIntegrationTests(TestDatabase db) : IntegrationTestBase(db)
{
    private static FollowService Follows(ApplicationDbContext ctx)
        => new(ctx, new NoticeWriter(ctx), SocialTestSeams.AfterCommit(ctx), NullLogger<FollowService>.Instance);

    private static MyBuddyService Buddies(ApplicationDbContext ctx) => new(ctx, new RecordingLiveHints(), NullLogger<MyBuddyService>.Instance);

    private static BuddyListService Lists(ApplicationDbContext ctx) => new(ctx, new RecordingLiveHints(), NullLogger<BuddyListService>.Instance);

    private async Task<List<Guid>> PeopleAsync(int n)
    {
        await using var ctx = CreateContext();
        var ids = await BuddySeed.UsersAsync(ctx, n);
        foreach (var id in ids) await SocialSeed.ProfileAsync(ctx, id);
        return ids;
    }

    private async Task<int> RowsAsync(Guid owner, Guid buddy)
    {
        await using var ctx = CreateContext();
        return await ctx.Set<BuddyLink>().CountAsync(b => b.UserId == owner && b.BuddyUserId == buddy);
    }

    [IntegrationFact]
    public async Task Only_a_follower_can_be_added_and_the_row_is_one_way()
    {
        var p = await PeopleAsync(2);
        await using (var ctx = CreateContext())
        {
            Assert.Equal(SocialErrorTypes.NotFollower, (await Buddies(ctx).AddAsync(p[0], p[1])).ErrorType);
            Assert.True((await Follows(ctx).FollowAsync(p[1], p[0])).Ok);
            Assert.True((await Buddies(ctx).AddAsync(p[0], p[1])).Value!.IsBuddy);
            Assert.True((await Buddies(ctx).AddAsync(p[0], p[1])).Value!.IsBuddy);
            Assert.Equal(SocialErrorTypes.NotFollower, (await Buddies(ctx).AddAsync(p[1], p[0])).ErrorType);
        }
        Assert.Equal(1, await RowsAsync(p[0], p[1]));
        Assert.Equal(0, await RowsAsync(p[1], p[0]));
    }

    [IntegrationFact]
    public async Task Remove_takes_only_my_row()
    {
        var p = await PeopleAsync(2);
        await using (var ctx = CreateContext())
        {
            await Follows(ctx).FollowAsync(p[1], p[0]);
            await Follows(ctx).FollowAsync(p[0], p[1]);
            Assert.True((await Buddies(ctx).AddAsync(p[0], p[1])).Ok);
            Assert.True((await Buddies(ctx).AddAsync(p[1], p[0])).Ok);
            await Lists(ctx).RemoveAsync(p[0], p[1]);
        }
        Assert.Equal(0, await RowsAsync(p[0], p[1]));
        Assert.Equal(1, await RowsAsync(p[1], p[0]));
    }

    [IntegrationFact]
    public async Task An_unknown_person_or_myself_cannot_be_added()
    {
        var p = await PeopleAsync(2);
        await using var ctx = CreateContext();
        Assert.Equal(SocialErrorTypes.PersonNotFound, (await Buddies(ctx).AddAsync(p[0], Guid.NewGuid())).ErrorType);
        Assert.Equal(SocialErrorTypes.PersonNotFound, (await Buddies(ctx).AddAsync(p[0], p[0])).ErrorType);
    }
}
