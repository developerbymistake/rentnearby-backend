using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RentNearBy.Core.Entities.Social;
using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.Social;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Support.Buddy;
using RentNearBy.Tests.Support.Social;

namespace RentNearBy.Tests.Social.Discover;

// Discover SQL against PostGIS (DESIGN 10.2). Needs RENTNEARBY_TEST_DB; skipped without it. No Redis: every page builds.
public class DiscoverIntegrationTests(TestDatabase db) : IntegrationTestBase(db)
{
    private static DiscoverService Service(ApplicationDbContext ctx)
        => new(ctx, new DiscoverCache(null, TimeProvider.System, NullLogger<DiscoverCache>.Instance), new FakeCityCatalog(), TimeProvider.System,
            NullLogger<DiscoverService>.Instance);

    private async Task<List<Guid>> PeopleAsync(int n, params int[] privateIdx)
    {
        await using var ctx = CreateContext();
        var ids = await BuddySeed.UsersAsync(ctx, n);
        for (var i = 0; i < ids.Count; i++) await SocialSeed.ProfileAsync(ctx, ids[i], isPrivate: privateIdx.Contains(i));
        return ids;
    }

    private async Task FollowAsync(Guid from, Guid to, string status = FollowStatuses.Accepted)
    {
        await using var ctx = CreateContext();
        ctx.Set<Follow>().Add(new Follow { FollowerId = from, FolloweeId = to, Status = status, CreatedAt = DateTime.UtcNow, EffectsAt = DateTime.UtcNow });
        await ctx.SaveChangesAsync();
    }

    private async Task<Guid> TripAsync(Guid owner, bool isPublic, DateOnly start)
    {
        await using var ctx = CreateContext();
        var id = Guid.NewGuid();
        ctx.Trips.Add(new Trip
        {
            Id = id, OwnerUserId = owner, CityKey = FakeCityCatalog.Manali, CityName = "Manali", Title = "Manali", Status = TripStatuses.Planned,
            PlanJson = "{}", DayCount = 3, SpotCount = 3, StartDate = start, EndDate = start.AddDays(2), InviteCode = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, IsPublic = isPublic, PublishedAt = isPublic ? DateTime.UtcNow : null,
            PostDays = isPublic ? (short)3 : null, PostStartMonth = isPublic ? SocialMonth.Key(start) : null, PostText = isPublic ? "Join" : null,
            Seats = isPublic ? (short)3 : null,
        });
        await ctx.SaveChangesAsync();
        return id;
    }

    private async Task<List<Guid>> AllAsync(Guid me, string chip = DiscoverChips.ForYou)
    {
        await using var ctx = CreateContext();
        var ids = new List<Guid>();
        DiscoverCursorValue? cursor = null;
        for (var i = 0; i < 20; i++)
        {
            var page = await Service(ctx).PageAsync(me, chip, cursor, 3, false);
            ids.AddRange(page.Items.Select(x => x.UserId));
            if (page.NextCursor is null) return ids;
            Assert.True(DiscoverCursor.TryDecode(page.NextCursor, out var v));
            cursor = v;
        }
        return ids;
    }

    [IntegrationFact]
    public async Task Mutual_people_show_and_followed_blocked_hidden_ones_never_do()
    {
        var p = await PeopleAsync(7);
        var me = p[0];
        await FollowAsync(me, p[1]);
        foreach (var target in p.Skip(2)) await FollowAsync(p[1], target);
        await FollowAsync(me, p[2]);                                   // followed: out
        await FollowAsync(me, p[3], FollowStatuses.Requested);         // requested: out
        await using (var ctx = CreateContext())
        {
            ctx.Set<UserBlock>().Add(new UserBlock { Id = Guid.NewGuid(), BlockerId = p[4], BlockedId = me, CreatedAt = DateTime.UtcNow });
            await ctx.SaveChangesAsync();
            await Service(ctx).HideAsync(me, p[5]);
            await Service(ctx).HideAsync(me, p[5]);                    // idempotent
            await Service(ctx).HideAsync(me, me);                      // no-op, no error
            await Service(ctx).HideAsync(me, Guid.NewGuid());          // unknown id: no-op, no error
        }
        var ids = await AllAsync(me);
        Assert.Equal([p[6]], ids);
        await using (var ctx = CreateContext())
        {
            var page = await Service(ctx).PageAsync(me, DiscoverChips.Mutual, null, 20, false);
            var reason = Assert.Single(page.Items).Reason;
            Assert.Equal(DiscoverReasonKinds.Mutual, reason.Kind);
            Assert.Equal(1, reason.MutualCount);
            Assert.Equal(1, await ctx.Set<DiscoverHide>().CountAsync(h => h.UserId == me));
        }
    }

    [IntegrationFact]
    public async Task Hidden_for_more_than_thirty_days_comes_back()
    {
        var p = await PeopleAsync(3);
        await FollowAsync(p[0], p[1]);
        await FollowAsync(p[1], p[2]);
        await using (var ctx = CreateContext())
        {
            ctx.Set<DiscoverHide>().Add(new DiscoverHide { UserId = p[0], HiddenUserId = p[2], HiddenAt = DateTime.UtcNow.AddDays(-31) });
            await ctx.SaveChangesAsync();
        }
        Assert.Equal([p[2]], await AllAsync(p[0]));
    }

    [IntegrationFact]
    public async Task Same_trip_comes_from_public_posts_only_and_carries_city_and_month()
    {
        var p = await PeopleAsync(3, 2);
        var start = IstTime.TodayIst(DateTime.UtcNow).AddDays(20);
        await TripAsync(p[0], false, start);
        await TripAsync(p[1], true, start.AddDays(2));
        await TripAsync(p[2], true, start);                            // private owner: never sameTrip
        await using var ctx = CreateContext();
        var page = await Service(ctx).PageAsync(p[0], DiscoverChips.SameTrip, null, 20, false);
        var item = Assert.Single(page.Items);
        Assert.Equal(p[1], item.UserId);
        Assert.Equal(("Manali", SocialMonth.Key(start.AddDays(2))), (item.Reason.CityName, item.Reason.Month!.Value));
        Assert.Equal("Manali", page.MyTrip!.CityName);
    }

    [IntegrationFact]
    public async Task A_private_follower_shows_only_as_follows_you_with_no_tags()
    {
        var p = await PeopleAsync(2, 1);
        await FollowAsync(p[1], p[0]);
        await using var ctx = CreateContext();
        var item = Assert.Single((await Service(ctx).PageAsync(p[0], DiscoverChips.ForYou, null, 20, false)).Items);
        Assert.True(item.IsPrivate);
        Assert.True(item.FollowsMe);
        Assert.Equal(DiscoverReasonKinds.FollowsYou, item.Reason.Kind);
        Assert.Null(item.Reason.Tags);
        Assert.Null(item.Reason.CityName);
    }

    [IntegrationFact]
    public async Task Similar_is_empty_for_a_private_person_i_do_not_follow_and_lists_his_following_otherwise()
    {
        var p = await PeopleAsync(3, 1);
        await FollowAsync(p[1], p[2]);
        await using (var ctx = CreateContext())
            Assert.Empty((await Service(ctx).SimilarAsync(p[0], p[1], 5)).Items);
        await FollowAsync(p[0], p[1]);
        await using (var ctx = CreateContext())
        {
            var item = Assert.Single((await Service(ctx).SimilarAsync(p[0], p[1], 5)).Items);
            Assert.Equal(p[2], item.UserId);
            Assert.Equal(DiscoverReasonKinds.Mutual, item.Reason.Kind);
        }
    }
}
