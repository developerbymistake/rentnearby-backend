using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities.Buddy;
using RentNearBy.Core.Interfaces.Buddy;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.Buddy.Be1;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Support.Buddy;

namespace RentNearBy.Tests.Buddy.Be1;

// Needs RENTNEARBY_TEST_DB (PostGIS); skipped without it. Calls the same service methods the BE1 handlers call.
public class Be1BuddyIntegrationTests(TestDatabase db) : IntegrationTestBase(db)
{
    private Guid _me;
    private Guid _him;
    private List<Guid> _people = [];
    private string _himName = string.Empty;

    private async Task<List<Guid>> PeopleAsync(int n)
    {
        await using var ctx = CreateContext();
        return await BuddySeed.UsersAsync(ctx, n);
    }

    private async Task<string> UsernameAsync(Guid id)
    {
        await using var ctx = CreateContext();
        return (await ctx.Users.AsNoTracking().Where(u => u.Id == id).Select(u => u.Username).SingleAsync())!;
    }

    private async Task BlockRowAsync(Guid blocker, Guid blocked)
    {
        await using var ctx = CreateContext();
        ctx.UserBlocks.Add(new UserBlock { Id = Guid.NewGuid(), BlockerId = blocker, BlockedId = blocked, CreatedAt = DateTime.UtcNow });
        await ctx.SaveChangesAsync();
    }

    private async Task<BuddyRequest?> RequestAsync(Guid id)
    {
        await using var ctx = CreateContext();
        return await ctx.Set<BuddyRequest>().AsNoTracking().SingleOrDefaultAsync(r => r.Id == id);
    }

    private async Task<int> BuddyRowsAsync(Guid a, Guid b)
    {
        await using var ctx = CreateContext();
        return await ctx.Set<BuddyLink>().CountAsync(x => (x.UserId == a && x.BuddyUserId == b) || (x.UserId == b && x.BuddyUserId == a));
    }

    // Many requests, buddies and blocks around the caller; the statement count must not move with them.
    private async Task SeedAroundAsync(ApplicationDbContext ctx, int rows)
    {
        _people = await BuddySeed.UsersAsync(ctx, rows * 4 + 2);
        _me = _people[0];
        _him = _people[1];
        _himName = (await ctx.Users.AsNoTracking().Where(u => u.Id == _him).Select(u => u.Username).SingleAsync())!;
        await BuddySeed.BuddiesAsync(ctx, _me, _people.Skip(2).Take(rows), seen: false);
        await BuddySeed.RequestsAsync(ctx, _me, _people.Skip(2 + rows).Take(rows));
        foreach (var from in _people.Skip(2 + rows * 2).Take(rows))
            await BuddySeed.RequestsAsync(ctx, from, [_me]);
        foreach (var blocked in _people.Skip(2 + rows * 3).Take(rows))
            ctx.UserBlocks.Add(new UserBlock { Id = Guid.NewGuid(), BlockerId = _me, BlockedId = blocked, CreatedAt = DateTime.UtcNow });
        await ctx.SaveChangesAsync();
    }

    // ---------- B1 ----------

    [IntegrationFact]
    public Task Find_QueryCount_IsFixed() => QueryAssert.SameCountAsync(Db, SeedAroundAsync,
        async ctx => Assert.True((await new Be1Kit(ctx).Find.FindAsync(_me, _himName)).Found), budget: 1);

    [IntegrationFact]
    public async Task Find_answers_the_same_for_unknown_blocked_inactive_and_malformed()
    {
        var p = await PeopleAsync(4);
        await BlockRowAsync(p[2], p[0]);
        await using (var ctx = CreateContext())
            await ctx.Users.Where(u => u.Id == p[3]).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
        await using var counting = Db.CreateCountingContext(out var counter);
        var kit = new Be1Kit(counting);
        foreach (var query in new[] { "Bakhli#99999998", await UsernameAsync(p[2]), await UsernameAsync(p[3]), "hello" })
        {
            counter.Reset();
            var r = await kit.Find.FindAsync(p[0], query);
            Assert.False(r.Found);
            Assert.Null(r.Person);
            Assert.Equal(1, counter.Count);
        }
        Assert.True((await kit.Find.FindAsync(p[0], (await UsernameAsync(p[1])).ToLowerInvariant())).Found);
    }

    [IntegrationFact]
    public async Task Find_reports_every_relation()
    {
        var p = await PeopleAsync(6);
        await using (var ctx = CreateContext())
        {
            await BuddySeed.BuddiesAsync(ctx, p[0], [p[1]]);
            await BuddySeed.RequestsAsync(ctx, p[0], [p[2]]);
            await BuddySeed.RequestsAsync(ctx, p[3], [p[0]]);
            await BuddySeed.RequestsAsync(ctx, p[0], [p[4]], BuddyRequestStatuses.Expired, TimeSpan.FromDays(20));
        }
        await using var c = CreateContext();
        var find = new Be1Kit(c).Find;
        Assert.Equal(BuddyRelations.Buddy, (await find.FindAsync(p[0], await UsernameAsync(p[1]))).Person!.Relation);
        Assert.Equal(BuddyRelations.RequestedByMe, (await find.FindAsync(p[0], await UsernameAsync(p[2]))).Person!.Relation);
        Assert.Equal(BuddyRelations.RequestedMe, (await find.FindAsync(p[0], await UsernameAsync(p[3]))).Person!.Relation);
        Assert.Equal(BuddyRelations.Cooldown, (await find.FindAsync(p[0], await UsernameAsync(p[4]))).Person!.Relation);
        Assert.Equal(BuddyRelations.None, (await find.FindAsync(p[0], await UsernameAsync(p[5]))).Person!.Relation);
        Assert.Equal(BuddyRelations.Self, (await find.FindAsync(p[0], await UsernameAsync(p[0]))).Person!.Relation);
    }

    // ---------- B2 ----------

    [IntegrationFact]
    public async Task Send_creates_once_and_answers_a_duplicate_with_the_same_request()
    {
        var p = await PeopleAsync(2);
        var client = Guid.NewGuid();
        await using var counting = Db.CreateCountingContext(out var counter);
        var kit = new Be1Kit(counting);
        counter.Reset();
        var first = await kit.Requests.SendAsync(p[0], p[1], BuddyRequestSources.Search, client);
        Assert.True(counter.Count + kit.Notices.Calls <= 8, $"B2 used {counter.Count + kit.Notices.Calls} statements");
        Assert.Equal(SendKind.Created, first.Value!.Kind);
        Assert.Single(kit.Notices.Upserts, n => n.UserId == p[1] && n.Kind == NoticeKinds.BuddyRequest);
        Assert.Single(kit.Notices.Pushes);

        var replay = await kit.Requests.SendAsync(p[0], p[1], BuddyRequestSources.Search, client);
        var again = await kit.Requests.SendAsync(p[0], p[1], BuddyRequestSources.Qr, Guid.NewGuid());
        Assert.Equal(SendKind.Existing, replay.Value!.Kind);
        Assert.Equal(SendKind.Existing, again.Value!.Kind);
        Assert.Equal(first.Value.Request!.RequestId, again.Value.Request!.RequestId);
        Assert.Single(kit.Notices.Pushes);
    }

    [IntegrationFact]
    public async Task Crossing_request_auto_accepts()
    {
        var p = await PeopleAsync(2);
        await using var ctx = CreateContext();
        var kit = new Be1Kit(ctx);
        await kit.Requests.SendAsync(p[1], p[0], BuddyRequestSources.Search, Guid.NewGuid());
        var crossing = await kit.Requests.SendAsync(p[0], p[1], BuddyRequestSources.Search, Guid.NewGuid());
        Assert.Equal(SendKind.Accepted, crossing.Value!.Kind);
        Assert.Equal(p[1], crossing.Value.Accepted!.Buddy.UserId);
        Assert.Equal(2, await BuddyRowsAsync(p[0], p[1]));
        Assert.Contains(kit.Notices.Pushes, x => x.PushKind == Core.Models.NotificationTypes.BuddyRequestAccepted && x.UserId == p[1]);
    }

    [IntegrationFact]
    public async Task Send_refusals_follow_the_rule_order()
    {
        var p = await PeopleAsync(6);
        await BlockRowAsync(p[2], p[0]);
        await using (var seed = CreateContext())
        {
            await BuddySeed.BuddiesAsync(seed, p[0], [p[3]]);
            await BuddySeed.RequestsAsync(seed, p[0], [p[4]], BuddyRequestStatuses.Cancelled, TimeSpan.FromDays(2));
            await seed.Users.Where(u => u.Id == p[5]).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
        }
        await using var ctx = CreateContext();
        var req = new Be1Kit(ctx).Requests;
        Assert.Equal(BuddyErrorTypes.SelfRequest, (await req.SendAsync(p[0], p[0], "Search", Guid.NewGuid())).ErrorType);
        Assert.Equal(BuddyErrorTypes.PersonNotFound, (await req.SendAsync(p[0], p[2], "Search", Guid.NewGuid())).ErrorType);
        Assert.Equal(BuddyErrorTypes.PersonNotFound, (await req.SendAsync(p[0], p[5], "Search", Guid.NewGuid())).ErrorType);
        Assert.Equal(BuddyErrorTypes.PersonNotFound, (await req.SendAsync(p[0], Guid.NewGuid(), "Search", Guid.NewGuid())).ErrorType);
        Assert.Equal(BuddyErrorTypes.AlreadyBuddies, (await req.SendAsync(p[0], p[3], "Search", Guid.NewGuid())).ErrorType);
        Assert.Equal(BuddyErrorTypes.TryLater, (await req.SendAsync(p[0], p[4], "Search", Guid.NewGuid())).ErrorType);
        await ctx.Users.Where(u => u.Id == p[0]).ExecuteUpdateAsync(s => s.SetProperty(u => u.BuddyRequestsHeldUntil, DateTime.UtcNow.AddDays(1)));
        Assert.Equal(BuddyErrorTypes.BuddyRequestsOnHold, (await req.SendAsync(p[0], p[1], "Search", Guid.NewGuid())).ErrorType);
    }

    [IntegrationFact]
    public async Task Waiting_limit_counts_silent_declines()
    {
        var p = await PeopleAsync(BuddyLimits.MaxWaitingRequests + 2);
        await using (var seed = CreateContext())
        {
            await BuddySeed.RequestsAsync(seed, p[0], p.Skip(1).Take(10));
            await BuddySeed.RequestsAsync(seed, p[0], p.Skip(11).Take(10), BuddyRequestStatuses.Declined);
        }
        await using var ctx = CreateContext();
        var result = await new Be1Kit(ctx).Requests.SendAsync(p[0], p[^1], "Search", Guid.NewGuid());
        Assert.Equal(BuddyErrorTypes.WaitingLimit, result.ErrorType);
    }

    [IntegrationFact]
    public async Task Buddy_caps_give_BuddyLimit_and_CannotAddNow()
    {
        var p = await PeopleAsync(3);
        var filler = await PeopleAsync(BuddyLimits.MaxBuddies);
        await using (var seed = CreateContext())
            await BuddySeed.BuddiesAsync(seed, p[1], filler);
        await using var ctx = CreateContext();
        var req = new Be1Kit(ctx).Requests;
        Assert.Equal(BuddyErrorTypes.CannotAddNow, (await req.SendAsync(p[0], p[1], "Search", Guid.NewGuid())).ErrorType);
        Assert.Equal(BuddyErrorTypes.BuddyLimit, (await req.SendAsync(p[1], p[2], "Search", Guid.NewGuid())).ErrorType);
    }

    // ---------- B5 / B6 / B7 ----------

    [IntegrationFact]
    public async Task Accept_is_idempotent_and_ends_the_notice()
    {
        var p = await PeopleAsync(2);
        Guid id;
        await using (var seed = CreateContext())
        {
            id = (await BuddySeed.RequestsAsync(seed, p[1], [p[0]]))[0];
            await BuddySeed.NoticesAsync(seed, p[0], [(NoticeKinds.BuddyRequest, NoticeKinds.NoTrip)]);
        }
        await using var ctx = CreateContext();
        var kit = new Be1Kit(ctx);
        var first = await kit.Requests.AcceptAsync(p[0], id);
        var second = await kit.Requests.AcceptAsync(p[0], id);
        Assert.True(first.Ok);
        Assert.True(second.Ok);
        Assert.Equal(p[1], second.Value!.Buddy.UserId);
        Assert.Single(kit.Notices.Pushes);
        Assert.Equal(2, kit.Notices.Upserts.Count);
        Assert.Contains(kit.Notices.Upserts, n => n.UserId == p[1] && n.Kind == NoticeKinds.BuddyAccepted && n.ActorUserId == p[0] && n.TripId == NoticeKinds.NoTrip);
        Assert.Contains(kit.Notices.Upserts, n => n.UserId == p[0] && n.Kind == NoticeKinds.NewBuddy && n.ActorUserId == p[1] && n.TripId == NoticeKinds.NoTrip);
        Assert.Equal(2, await BuddyRowsAsync(p[0], p[1]));
        Assert.False(await ctx.Set<UserNotice>().AnyAsync(n => n.UserId == p[0]));
        Assert.Equal(BuddyErrorTypes.RequestEnded, (await kit.Requests.AcceptAsync(p[1], id)).ErrorType);
    }

    [IntegrationFact]
    public async Task Double_accept_race_makes_one_pair_and_one_push()
    {
        var p = await PeopleAsync(2);
        Guid id;
        await using (var seed = CreateContext())
            id = (await BuddySeed.RequestsAsync(seed, p[1], [p[0]]))[0];
        await using var a = CreateContext();
        await using var b = CreateContext();
        var ka = new Be1Kit(a);
        var kb = new Be1Kit(b);
        var results = await Task.WhenAll(ka.Requests.AcceptAsync(p[0], id), kb.Requests.AcceptAsync(p[0], id));
        Assert.All(results, r => Assert.True(r.Ok));
        Assert.Equal(1, ka.Notices.Pushes.Count + kb.Notices.Pushes.Count);
        Assert.Equal(2, await BuddyRowsAsync(p[0], p[1]));
    }

    [IntegrationFact]
    public async Task Decline_is_silent_for_the_sender()
    {
        var p = await PeopleAsync(2);
        Guid id;
        await using (var seed = CreateContext())
            id = (await BuddySeed.RequestsAsync(seed, p[0], [p[1]]))[0];
        await using var ctx = CreateContext();
        var kit = new Be1Kit(ctx);
        Assert.True(await kit.Requests.DeclineAsync(p[1], id));
        Assert.True(await kit.Requests.DeclineAsync(p[1], id));
        Assert.False(await kit.Requests.DeclineAsync(p[0], id));
        Assert.Empty(kit.Notices.Pushes);
        var sent = await kit.Lists.SentAsync(p[0], null, 50);
        Assert.Equal(SentStates.Waiting, Assert.Single(sent.Items).State);
        Assert.Equal(BuddyRelations.RequestedByMe, (await kit.Find.FindAsync(p[0], await UsernameAsync(p[1]))).Person!.Relation);
        Assert.Equal(SendKind.Existing, (await kit.Requests.SendAsync(p[0], p[1], "Search", Guid.NewGuid())).Value!.Kind);
        Assert.Empty((await kit.Lists.ReceivedAsync(p[1], null, 50)).Items);
    }

    [IntegrationFact]
    public async Task Cancel_after_accept_is_AlreadyBuddies_and_cancel_hides_a_silent_row()
    {
        var p = await PeopleAsync(3);
        Guid accepted, declined;
        await using (var seed = CreateContext())
        {
            accepted = (await BuddySeed.RequestsAsync(seed, p[0], [p[1]], BuddyRequestStatuses.Accepted))[0];
            declined = (await BuddySeed.RequestsAsync(seed, p[0], [p[2]], BuddyRequestStatuses.Declined))[0];
        }
        await using var ctx = CreateContext();
        var kit = new Be1Kit(ctx);
        Assert.Equal(BuddyErrorTypes.AlreadyBuddies, (await kit.Requests.CancelAsync(p[0], accepted)).ErrorType);
        Assert.True((await kit.Requests.CancelAsync(p[0], declined)).Ok);
        Assert.True((await kit.Requests.CancelAsync(p[0], declined)).Ok);
        Assert.Equal(BuddyErrorTypes.RequestEnded, (await kit.Requests.CancelAsync(p[2], declined)).ErrorType);
        Assert.Equal(BuddyRequestStatuses.Cancelled, (await RequestAsync(declined))!.Status);
        Assert.Empty((await kit.Lists.SentAsync(p[0], null, 50)).Items);
    }

    // ---------- reads: fixed statement counts at 3 and 30 rows ----------

    [IntegrationFact]
    public Task Received_QueryCount_IsFixed() => QueryAssert.SameCountAsync(Db, SeedAroundAsync,
        async ctx => Assert.NotEmpty((await new Be1Kit(ctx).Lists.ReceivedAsync(_me, null, 50)).Items), budget: 1);

    [IntegrationFact]
    public Task Sent_QueryCount_IsFixed() => QueryAssert.SameCountAsync(Db, SeedAroundAsync,
        async ctx => Assert.NotEmpty((await new Be1Kit(ctx).Lists.SentAsync(_me, null, 50)).Items), budget: 1);

    [IntegrationFact]
    public Task Buddies_QueryCount_IsFixed() => QueryAssert.SameCountAsync(Db, SeedAroundAsync,
        async ctx => Assert.NotEmpty((await new Be1Kit(ctx).Lists.BuddiesAsync(_me, null, 100, null))!.Items), budget: 2);

    [IntegrationFact]
    public Task Summary_QueryCount_IsFixed() => QueryAssert.SameCountAsync(Db, SeedAroundAsync,
        async ctx => Assert.True((await new Be1Kit(ctx).Lists.SummaryAsync(_me)).Buddies > 0), budget: 1);

    [IntegrationFact]
    public Task Blocks_QueryCount_IsFixed() => QueryAssert.SameCountAsync(Db, SeedAroundAsync,
        async ctx => Assert.NotEmpty((await new Be1Kit(ctx).Lists.BlocksAsync(_me, null, 50)).Items), budget: 1);

    [IntegrationFact]
    public Task BlockContext_QueryCount_IsFixed() => QueryAssert.SameCountAsync(Db, SeedAroundAsync,
        async ctx => Assert.NotNull(await new Be1Kit(ctx).Lists.BlockContextAsync(_me, _him)), budget: 2);

    [IntegrationFact]
    public Task BuddiesForTrip_QueryCount_IsFixed()
    {
        var trip = Guid.Empty;
        return QueryAssert.SameCountAsync(Db,
            async (ctx, rows) =>
            {
                await SeedAroundAsync(ctx, rows);
                trip = await BuddySeed.TripAsync(ctx, _me);
                await BuddySeed.MembersAsync(ctx, trip, _me, _people.Skip(2).Take(rows));
            },
            async ctx =>
            {
                var page = await new Be1Kit(ctx).Lists.BuddiesAsync(_me, null, 100, trip);
                Assert.All(page!.Items, i => Assert.Equal(BuddyTripStates.Joined, i.TripState));
            }, budget: 2);
    }

    [IntegrationFact]
    public async Task Crossing_requests_in_parallel_make_one_pair()
    {
        var p = await PeopleAsync(2);
        await using var a = CreateContext();
        await using var b = CreateContext();
        var results = await Task.WhenAll(
            new Be1Kit(a).Requests.SendAsync(p[0], p[1], "Search", Guid.NewGuid()),
            new Be1Kit(b).Requests.SendAsync(p[1], p[0], "Search", Guid.NewGuid()));
        Assert.All(results, r => Assert.True(r.Ok));
        Assert.Single(results, r => r.Value!.Kind == SendKind.Created);
        Assert.Single(results, r => r.Value!.Kind == SendKind.Accepted);
        Assert.Equal(2, await BuddyRowsAsync(p[0], p[1]));
    }

    [IntegrationFact]
    public async Task Parallel_sends_never_pass_the_waiting_cap()
    {
        var p = await PeopleAsync(BuddyLimits.MaxWaitingRequests + 3);
        await using (var seed = CreateContext())
            await BuddySeed.RequestsAsync(seed, p[0], p.Skip(1).Take(BuddyLimits.MaxWaitingRequests - 1));
        await using var a = CreateContext();
        await using var b = CreateContext();
        var results = await Task.WhenAll(
            new Be1Kit(a).Requests.SendAsync(p[0], p[^1], "Search", Guid.NewGuid()),
            new Be1Kit(b).Requests.SendAsync(p[0], p[^2], "Search", Guid.NewGuid()));
        Assert.Single(results, r => r.Ok);
        Assert.Single(results, r => r.ErrorType == BuddyErrorTypes.WaitingLimit);
    }

    [IntegrationFact]
    public async Task Summary_counts_buddies_new_received_and_blocked()
    {
        var p = await PeopleAsync(5);
        await using (var seed = CreateContext())
        {
            await BuddySeed.BuddiesAsync(seed, p[0], [p[1]], seen: true);
            await BuddySeed.BuddiesAsync(seed, p[0], [p[2]], seen: false);
            await BuddySeed.RequestsAsync(seed, p[3], [p[0]]);
        }
        await BlockRowAsync(p[0], p[4]);
        await using var ctx = CreateContext();
        var s = await new Be1Kit(ctx).Lists.SummaryAsync(p[0]);
        Assert.Equal((2, 1, 1, 1), (s.Buddies, s.NewBuddies, s.ReceivedPending, s.Blocked));
    }

    [IntegrationFact]
    public async Task Buddies_pages_by_name_and_reads_trip_state()
    {
        var p = await PeopleAsync(4);
        Guid trip;
        await using (var seed = CreateContext())
        {
            await BuddySeed.BuddiesAsync(seed, p[0], p.Skip(1));
            trip = await BuddySeed.TripAsync(seed, p[0]);
            await BuddySeed.MembersAsync(seed, trip, p[0], [p[1]]);
            await BuddySeed.MembersAsync(seed, trip, p[0], [p[2]], TripMemberStatuses.Invited);
        }
        await using var ctx = CreateContext();
        var lists = new Be1Kit(ctx).Lists;
        var first = await lists.BuddiesAsync(p[0], null, 2, trip);
        Assert.Equal(3, first!.Count);
        Assert.Equal(2, first.Items.Count);
        Assert.NotNull(first.NextCursor);
        Assert.True(BuddyCursors.TryDecodeName(first.NextCursor, out var name, out var id));
        var second = await lists.BuddiesAsync(p[0], (name, id), 2, trip);
        Assert.Single(second!.Items);
        Assert.Null(second.NextCursor);
        var states = first.Items.Concat(second.Items).ToDictionary(i => i.UserId, i => i.TripState);
        Assert.Equal(BuddyTripStates.Joined, states[p[1]]);
        Assert.Equal(BuddyTripStates.Invited, states[p[2]]);
        Assert.Equal(BuddyTripStates.None, states[p[3]]);
        Assert.Null(await lists.BuddiesAsync(p[1], null, 10, trip));
    }

    [IntegrationFact]
    public async Task Seen_and_remove()
    {
        var p = await PeopleAsync(2);
        await using (var seed = CreateContext())
            await BuddySeed.BuddiesAsync(seed, p[0], [p[1]], seen: false);
        await using var ctx = CreateContext();
        var lists = new Be1Kit(ctx).Lists;
        await lists.SeenAsync(p[0], [p[1]]);
        Assert.False((await lists.BuddiesAsync(p[0], null, 10, null))!.Items.Single().IsNew);
        await lists.RemoveAsync(p[1], p[0]);
        await lists.RemoveAsync(p[1], p[0]);
        Assert.Equal(0, await BuddyRowsAsync(p[0], p[1]));
    }

    // ---------- B12 / B13 / B17 ----------

    [IntegrationFact]
    public async Task Block_closes_requests_removes_buddies_and_is_idempotent()
    {
        var p = await PeopleAsync(3);
        Guid fromHim;
        await using (var seed = CreateContext())
        {
            fromHim = (await BuddySeed.RequestsAsync(seed, p[1], [p[0]]))[0];
            await BuddySeed.BuddiesAsync(seed, p[0], [p[2]]);
        }
        await using var counting = Db.CreateCountingContext(out var counter);
        var kit = new Be1Kit(counting);
        counter.Reset();
        var outcome = await kit.Blocks.BlockAsync(p[0], p[1]);
        Assert.True(counter.Count + kit.Notices.Calls <= 9, $"B12 used {counter.Count + kit.Notices.Calls} statements");
        Assert.Equal(BlockResult.Done, outcome.Result);
        var closed = (await RequestAsync(fromHim))!;
        Assert.Equal(BuddyRequestStatuses.ClosedByBlock, closed.Status);
        Assert.True(closed.BlockedSender);
        Assert.Single(kit.Participant.Calls);
        Assert.Equal(1, kit.Closure.Blocks);
        Assert.Equal(BlockResult.Done, (await kit.Blocks.BlockAsync(p[0], p[1])).Result);
        await kit.Blocks.BlockAsync(p[0], p[2]);
        Assert.Equal(0, await BuddyRowsAsync(p[0], p[2]));
        Assert.Equal(BlockResult.TargetMissing, (await kit.Blocks.BlockAsync(p[0], Guid.NewGuid())).Result);
        Assert.Equal(2, (await kit.Lists.BlocksAsync(p[0], null, 50)).Count);
        Assert.False((await kit.Find.FindAsync(p[1], await UsernameAsync(p[0]))).Found);

        await kit.Blocks.UnblockAsync(p[0], p[1]);
        await kit.Blocks.UnblockAsync(p[0], p[1]);
        Assert.Equal(1, (await kit.Lists.BlocksAsync(p[0], null, 50)).Count);
    }

    [IntegrationFact]
    public async Task Block_context_lists_trips_in_common_both_ways()
    {
        var p = await PeopleAsync(3);
        Guid mine, his;
        await using (var seed = CreateContext())
        {
            mine = await BuddySeed.TripAsync(seed, p[0], title: "Mine");
            his = await BuddySeed.TripAsync(seed, p[1], title: "His");
            await BuddySeed.MembersAsync(seed, mine, p[0], [p[1]]);
            await BuddySeed.MembersAsync(seed, his, p[1], [p[0]]);
        }
        await using var ctx = CreateContext();
        var lists = new Be1Kit(ctx).Lists;
        var context = await lists.BlockContextAsync(p[0], p[1]);
        Assert.Equal(2, context!.TripsInCommon.Count);
        Assert.Contains(context.TripsInCommon, t => t.TripId == mine && t.MyRole == "Owner");
        Assert.Contains(context.TripsInCommon, t => t.TripId == his && t.MyRole == "Viewer");
        Assert.Empty((await lists.BlockContextAsync(p[0], p[2]))!.TripsInCommon);
        await BlockRowAsync(p[1], p[0]);
        Assert.Null(await lists.BlockContextAsync(p[0], p[1]));
    }

    [IntegrationFact]
    public async Task Block_context_hides_the_username_of_a_co_viewer_who_is_not_a_buddy()
    {
        var p = await PeopleAsync(3);
        await using (var seed = CreateContext())
        {
            var trip = await BuddySeed.TripAsync(seed, p[0]);
            await BuddySeed.MembersAsync(seed, trip, p[0], [p[1], p[2]]);
        }
        await using var ctx = CreateContext();
        var lists = new Be1Kit(ctx).Lists;
        var coViewer = (await lists.BlockContextAsync(p[1], p[2]))!.Person;
        Assert.Null(coViewer.Username);
        Assert.False(string.IsNullOrEmpty(coViewer.Name));
        Assert.NotNull((await lists.BlockContextAsync(p[1], p[0]))!.Person.Username);
        Assert.NotNull((await lists.BlockContextAsync(p[0], p[2]))!.Person.Username);
    }

    [IntegrationFact]
    public async Task Send_without_a_find_is_PersonNotFound_and_writes_nothing()
    {
        var p = await PeopleAsync(2);
        await using var ctx = CreateContext();
        var kit = new Be1Kit(ctx);
        var result = await kit.Requests.SendAsync(p[0], p[1], BuddyRequestSources.Search, Guid.NewGuid(), foundByUsername: false);
        Assert.Equal(BuddyErrorTypes.PersonNotFound, result.ErrorType);
        Assert.Empty(kit.Notices.Pushes);
        await kit.Requests.SendAsync(p[1], p[0], BuddyRequestSources.Search, Guid.NewGuid());
        var crossing = await kit.Requests.SendAsync(p[0], p[1], BuddyRequestSources.Search, Guid.NewGuid(), foundByUsername: false);
        Assert.Equal(SendKind.Accepted, crossing.Value!.Kind);
    }

    // ---------- B15 / B16 ----------

    [IntegrationFact]
    public async Task Report_files_once_publishes_to_admins_and_auto_holds_after_three_people()
    {
        var p = await PeopleAsync(4);
        var spam = Guid.Parse("c2000000-0000-0000-0000-000000000001");
        await using var ctx = CreateContext();
        var kit = new Be1Kit(ctx);
        Assert.Equal(ReportFiled.InvalidReason, await kit.Reports.ReportAsync(p[1], p[0], Guid.NewGuid(), null, "Find"));
        Assert.Equal(ReportFiled.Filed, await kit.Reports.ReportAsync(p[1], p[0], spam, "x", "Find"));
        Assert.Equal(ReportFiled.Filed, await kit.Reports.ReportAsync(p[1], p[0], spam, "x", "Find"));
        Assert.Equal(ReportFiled.Filed, await kit.Reports.ReportAsync(p[1], Guid.NewGuid(), spam, null, "Find"));
        Assert.Single(kit.Publisher.Published);
        Assert.Contains("\"NotifyOwner\":false", kit.Publisher.Published[0].Message);
        Assert.Equal(1, await ctx.ListingReports.CountAsync(r => r.ReportedUserId == p[0] && r.ListingType == ReportScopes.User));
        Assert.Null(await ctx.Users.Where(u => u.Id == p[0]).Select(u => u.BuddyRequestsHeldUntil).SingleAsync());
        await kit.Reports.ReportAsync(p[2], p[0], spam, null, "Find");
        await kit.Reports.ReportAsync(p[3], p[0], spam, null, "Find");
        Assert.NotNull(await ctx.Users.AsNoTracking().Where(u => u.Id == p[0]).Select(u => u.BuddyRequestsHeldUntil).SingleAsync());
        var counts = await kit.Reports.CountsAsync(p[0]);
        Assert.Equal((3, 3), (counts.Reports30d, counts.Reporters30d));
        Assert.Equal(4, (await kit.Reports.ReasonsAsync()).Count);
    }

    // ---------- job, account deletion ----------

    [IntegrationFact]
    public async Task Expiry_job_expires_overdue_requests_and_drops_empty_notices()
    {
        var p = await PeopleAsync(3);
        Guid old, fresh;
        await using (var seed = CreateContext())
        {
            old = (await BuddySeed.RequestsAsync(seed, p[1], [p[0]], age: TimeSpan.FromDays(15)))[0];
            fresh = (await BuddySeed.RequestsAsync(seed, p[2], [p[1]]))[0];
            await BuddySeed.NoticesAsync(seed, p[0], [(NoticeKinds.BuddyRequest, NoticeKinds.NoTrip)]);
            await BuddySeed.NoticesAsync(seed, p[1], [(NoticeKinds.BuddyRequest, NoticeKinds.NoTrip)]);
        }
        await using var ctx = CreateContext();
        Assert.True(await new BuddyRequestExpiryJob(ctx).RunAsync(default) >= 1);
        Assert.Equal(BuddyRequestStatuses.Expired, (await RequestAsync(old))!.Status);
        Assert.Equal(BuddyRequestStatuses.Pending, (await RequestAsync(fresh))!.Status);
        Assert.False(await ctx.Set<UserNotice>().AnyAsync(n => n.UserId == p[0]));
        Assert.True(await ctx.Set<UserNotice>().AnyAsync(n => n.UserId == p[1]));
    }

    [IntegrationFact]
    public async Task Account_deletion_drops_his_blocks_and_notices_he_alone_caused()
    {
        var p = await PeopleAsync(3);
        await using (var seed = CreateContext())
        {
            await BuddySeed.RequestsAsync(seed, p[0], [p[1]]);
            await BuddySeed.NoticesAsync(seed, p[1], [(NoticeKinds.BuddyRequest, NoticeKinds.NoTrip)]);
        }
        await BlockRowAsync(p[0], p[2]);
        await BlockRowAsync(p[2], p[0]);
        await using var ctx = CreateContext();
        await new BuddyAccountDeletion(ctx).OnDeletingAsync(p[0]);
        Assert.False(await ctx.UserBlocks.AnyAsync(b => b.BlockerId == p[0] || b.BlockedId == p[0]));
        Assert.False(await ctx.Set<UserNotice>().AnyAsync(n => n.UserId == p[1]));
    }

    [IntegrationFact]
    public async Task Account_deletion_holds_the_pair_locks_of_his_buddies_and_open_requests()
    {
        var p = await PeopleAsync(3);
        await using (var seed = CreateContext())
        {
            await BuddySeed.BuddiesAsync(seed, p[0], [p[1]]);
            await BuddySeed.RequestsAsync(seed, p[2], [p[0]]);
        }
        await using var deleting = CreateContext();
        await using var tx = await deleting.Database.BeginTransactionAsync();
        await new BuddyAccountDeletion(deleting).LockAsync(p[0]);

        await using var other = CreateContext();
        await using var otherTx = await other.Database.BeginTransactionAsync();
        var locked = await other.Database.SqlQueryRaw<bool>(
            "SELECT pg_try_advisory_xact_lock(hashtextextended(@k, 0)) AS \"Value\"",
            new Npgsql.NpgsqlParameter("k", BuddyPairs.PairLockKey(p[0], p[2]))).ToListAsync();
        Assert.False(locked[0]);
        await tx.RollbackAsync();
    }
}
