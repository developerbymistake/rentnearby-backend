using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Entities.Buddy;
using RentNearBy.Core.Entities.Social;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.Buddy.Be4;
using RentNearBy.Infrastructure.Services.Social;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Support.Buddy;
using RentNearBy.Tests.Support.Social;
using static RentNearBy.Infrastructure.Services.Social.SocialSql;

namespace RentNearBy.Tests.Social.Discover;

// Discover DESIGN 5.2 / 5.3 / 10.2: FollowEffectsJob over Follows rows with EffectsAt NULL (seeded directly, the job only reads
// the row). Needs RENTNEARBY_TEST_DB; skipped without it.
public class FollowEffectsJobIntegrationTests(TestDatabase db) : IntegrationTestBase(db)
{
    private static FollowEffectsWork Work(ApplicationDbContext ctx, RecordingLiveHints? hints = null)
        => new(ctx, new NoticeWriter(ctx), SocialTestSeams.AfterCommit(ctx, hints: hints), NullLogger<FollowEffectsWork>.Instance);

    private async Task<List<Guid>> PeopleAsync(int n, bool notifyFollows = true, bool isPrivate = false)
    {
        await using var ctx = CreateContext();
        var ids = await BuddySeed.UsersAsync(ctx, n);
        foreach (var id in ids) await SocialSeed.ProfileAsync(ctx, id, isPrivate: isPrivate, notifyFollows: notifyFollows); // batch-loop: test seed
        return ids;
    }

    private async Task FollowAsync(Guid follower, Guid followee, string status = FollowStatuses.Accepted)
    {
        await using var ctx = CreateContext();
        await ctx.Database.ExecuteSqlRawAsync("""
            INSERT INTO "Follows" ("FollowerId", "FolloweeId", "Status", "CreatedAt", "AcceptedAt", "EffectsAt")
            VALUES (@a, @b, @s, now(), CASE WHEN @s = 'A' THEN now() END, NULL)
            """, [Uuid("a", follower), Uuid("b", followee), Text("s", status)]);
    }

    private async Task RunAsync(RecordingLiveHints? hints = null)
    {
        await using var ctx = CreateContext();
        await Work(ctx, hints).RunOnceAsync(CancellationToken.None);
    }

    private async Task MakeDueAsync(Guid receiver)
    {
        await using var ctx = CreateContext();
        await ctx.Database.ExecuteSqlRawAsync(
            """UPDATE "FollowPushGroups" SET "DueAt" = now() - interval '1 second' WHERE "ReceiverId" = @u AND "DueAt" IS NOT NULL""",
            [Uuid("u", receiver)]);
    }

    private async Task<int> FlushAsync()
    {
        await using var ctx = CreateContext();
        return await Work(ctx).FlushGroupsAsync(CancellationToken.None);
    }

    private async Task<List<OutboxMessage>> PushesAsync(Guid receiver)
    {
        await using var ctx = CreateContext();
        var prefix = $"fol:{receiver:N}:";
        return await ctx.OutboxMessages.AsNoTracking().Where(o => o.Key.StartsWith(prefix)).ToListAsync();
    }

    private async Task<FollowPushGroup?> GroupAsync(Guid receiver, string kind)
    {
        await using var ctx = CreateContext();
        return await ctx.Set<FollowPushGroup>().AsNoTracking().SingleOrDefaultAsync(g => g.ReceiverId == receiver && g.Kind == kind);
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

    private async Task<int> NoticesAsync(Guid user, string kind)
    {
        await using var ctx = CreateContext();
        return await ctx.Set<UserNotice>().CountAsync(n => n.UserId == user && n.Kind == kind);
    }

    [IntegrationFact]
    public async Task A_cycle_writes_bells_counts_hints_and_marks_the_row_done()
    {
        var p = await PeopleAsync(3);
        var priv = (await PeopleAsync(1, isPrivate: true))[0];
        await FollowAsync(p[1], p[0]);
        await FollowAsync(p[2], priv, FollowStatuses.Requested);
        var hints = new RecordingLiveHints();
        await RunAsync(hints);

        Assert.Equal(1, await NoticesAsync(p[0], NoticeKinds.NewFollower));
        Assert.Equal(1, await NoticesAsync(priv, NoticeKinds.FollowRequest));
        Assert.Equal((1, 0), await CountersAsync(p[0]));
        Assert.Equal((0, 1), await CountersAsync(p[1]));
        Assert.Equal((0, 0), await CountersAsync(priv));
        Assert.Contains(hints.SocialChanged, h => h.Scope == SocialLiveScopes.Followers && h.UserIds.Contains(p[0]));
        Assert.Contains(hints.SocialChanged, h => h.Scope == SocialLiveScopes.FollowRequests && h.UserIds.Contains(priv));
        await using var ctx = CreateContext();
        Assert.False(await ctx.Set<Follow>().AnyAsync(f => (f.FolloweeId == p[0] || f.FolloweeId == priv) && f.EffectsAt == null));
    }

    [IntegrationFact]
    public async Task A_replayed_cycle_changes_nothing()
    {
        var p = await PeopleAsync(2);
        await FollowAsync(p[1], p[0]);
        await RunAsync();
        await RunAsync();
        await using (var ctx = CreateContext())
        {
            await ctx.Database.ExecuteSqlRawAsync("""INSERT INTO "FollowCountDirty" ("UserId") VALUES (@u) ON CONFLICT DO NOTHING""", [Uuid("u", p[0])]);
        }
        await RunAsync();
        Assert.Equal((1, 0), await CountersAsync(p[0]));
        await using var check = CreateContext();
        Assert.Equal(1, (await check.Set<UserNotice>().SingleAsync(n => n.UserId == p[0] && n.Kind == NoticeKinds.NewFollower)).Count);
    }

    [IntegrationFact]
    public async Task An_unfollow_before_the_job_sends_nothing()
    {
        var p = await PeopleAsync(2);
        await FollowAsync(p[1], p[0]);
        await using (var ctx = CreateContext())
        {
            await ctx.Database.ExecuteSqlRawAsync("""DELETE FROM "Follows" WHERE "FollowerId" = @a AND "FolloweeId" = @b""", [Uuid("a", p[1]), Uuid("b", p[0])]);
        }
        await RunAsync();
        Assert.Equal(0, await NoticesAsync(p[0], NoticeKinds.NewFollower));
        Assert.Null(await GroupAsync(p[0], FollowStatuses.Accepted));
        Assert.Empty(await PushesAsync(p[0]));
    }

    [IntegrationFact]
    public async Task An_accept_before_the_job_is_never_a_new_follower()
    {
        var p = await PeopleAsync(2);
        await FollowAsync(p[1], p[0], FollowStatuses.Requested);
        await using (var ctx = CreateContext())
        {
            await ctx.Database.ExecuteSqlRawAsync("""
                UPDATE "Follows" SET "Status" = 'A', "AcceptedAt" = now(), "EffectsAt" = COALESCE("EffectsAt", now())
                WHERE "FollowerId" = @a AND "FolloweeId" = @b
                """, [Uuid("a", p[1]), Uuid("b", p[0])]);
        }
        await RunAsync();
        Assert.Equal(0, await NoticesAsync(p[0], NoticeKinds.NewFollower));
        Assert.Null(await GroupAsync(p[0], FollowStatuses.Accepted));
    }

    [IntegrationFact]
    public async Task Three_follows_in_the_window_give_one_grouped_push()
    {
        var p = await PeopleAsync(4);
        await FollowAsync(p[1], p[0]);
        await RunAsync();
        await FollowAsync(p[2], p[0]);
        await FollowAsync(p[3], p[0]);
        await RunAsync();
        var open = await GroupAsync(p[0], FollowStatuses.Accepted);
        Assert.NotNull(open!.DueAt);
        Assert.True(open.DueAt > DateTime.UtcNow.AddSeconds(30));

        await MakeDueAsync(p[0]);
        await FlushAsync();
        var pushes = await PushesAsync(p[0]);
        Assert.Single(pushes);
        Assert.Contains("and 2 others followed you", pushes[0].Payload);
        Assert.Contains("Person 4", pushes[0].Payload);
        var closed = await GroupAsync(p[0], FollowStatuses.Accepted);
        Assert.Null(closed!.PendingSince);
        Assert.Null(closed.DueAt);
        Assert.NotNull(closed.LastSentAt);
    }

    [IntegrationFact]
    public async Task A_follow_inside_the_gap_waits_one_hour_after_the_last_push()
    {
        var p = await PeopleAsync(3);
        await FollowAsync(p[1], p[0]);
        await RunAsync();
        await MakeDueAsync(p[0]);
        await FlushAsync();
        var sent = (await GroupAsync(p[0], FollowStatuses.Accepted))!.LastSentAt!.Value;

        await FollowAsync(p[2], p[0]);
        await RunAsync();
        var g = await GroupAsync(p[0], FollowStatuses.Accepted);
        Assert.True(g!.DueAt >= sent.Add(SocialPushCopy.FollowPushGap).AddSeconds(-1));
        await MakeDueAsync(p[0]);
        await FlushAsync();
        var pushes = await PushesAsync(p[0]);
        Assert.Equal(2, pushes.Count);
        Assert.All(pushes, x => Assert.DoesNotContain(" other", x.Payload));
        Assert.Single(pushes, x => x.Payload.Contains("Person 3 followed you"));
    }

    [IntegrationFact]
    public async Task Requests_are_grouped_separately_from_followers()
    {
        var priv = (await PeopleAsync(1, isPrivate: true))[0];
        var fans = await PeopleAsync(2);
        await FollowAsync(fans[0], priv, FollowStatuses.Requested);
        await FollowAsync(fans[1], priv, FollowStatuses.Requested);
        await RunAsync();
        Assert.NotNull(await GroupAsync(priv, FollowStatuses.Requested));
        Assert.Null(await GroupAsync(priv, FollowStatuses.Accepted));
        await MakeDueAsync(priv);
        await FlushAsync();
        var pushes = await PushesAsync(priv);
        Assert.Single(pushes);
        Assert.Contains($"fol:{priv:N}:R:", pushes[0].Key);
        Assert.Contains("and 1 other want to follow you", pushes[0].Payload);
    }

    [IntegrationFact]
    public async Task Follows_off_sends_nothing_and_closes_the_group()
    {
        var quiet = (await PeopleAsync(1, notifyFollows: false))[0];
        var fan = (await PeopleAsync(1))[0];
        await FollowAsync(fan, quiet);
        await RunAsync();
        Assert.Equal(1, await NoticesAsync(quiet, NoticeKinds.NewFollower));
        await MakeDueAsync(quiet);
        await FlushAsync();
        Assert.Empty(await PushesAsync(quiet));
        var g = await GroupAsync(quiet, FollowStatuses.Accepted);
        Assert.Null(g!.DueAt);
        Assert.Null(g.LastSentAt);
    }

    [IntegrationFact]
    public async Task A_blocked_follower_is_left_out_of_count_and_name()
    {
        var p = await PeopleAsync(3);
        await FollowAsync(p[1], p[0]);
        await FollowAsync(p[2], p[0]);
        await RunAsync();
        await using (var ctx = CreateContext())
        {
            ctx.UserBlocks.Add(new UserBlock { Id = Guid.NewGuid(), BlockerId = p[0], BlockedId = p[2], CreatedAt = DateTime.UtcNow });
            await ctx.SaveChangesAsync();
        }
        await MakeDueAsync(p[0]);
        await FlushAsync();
        var push = Assert.Single(await PushesAsync(p[0]));
        Assert.Contains("Person 2 followed you", push.Payload);
        Assert.DoesNotContain("Person 3", push.Payload);
    }

    [IntegrationFact]
    public async Task A_flush_replay_writes_one_outbox_row()
    {
        var p = await PeopleAsync(2);
        await FollowAsync(p[1], p[0]);
        await RunAsync();
        var since = (await GroupAsync(p[0], FollowStatuses.Accepted))!.PendingSince!.Value;
        await MakeDueAsync(p[0]);
        await FlushAsync();
        await FlushAsync();
        Assert.Null((await GroupAsync(p[0], FollowStatuses.Accepted))!.DueAt);
        await using (var ctx = CreateContext())
        {
            await ctx.Database.ExecuteSqlRawAsync(
                """UPDATE "FollowPushGroups" SET "PendingSince" = @s, "DueAt" = now() - interval '1 second' WHERE "ReceiverId" = @u AND "Kind" = 'A'""",
                [At("s", since), Uuid("u", p[0])]);
        }
        await FlushAsync();
        Assert.Single(await PushesAsync(p[0]));
    }
}
