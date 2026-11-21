using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Entities.Buddy;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.Buddy;
using RentNearBy.Infrastructure.Services.Buddy.Be2;
using RentNearBy.Infrastructure.Services.Buddy.Be3;
using RentNearBy.Infrastructure.Services.Buddy.Be4;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Support.Buddy;

namespace RentNearBy.Tests.Buddy.Be2;

// Needs RENTNEARBY_TEST_DB (PostGIS); skipped without it. Endpoint behaviour (errors, races) and query budgets of
// contract 7.1 on the service methods the handlers call, with 3 and 30 rows.
public class TripSharingIntegrationTests(TestDatabase db) : IntegrationTestBase(db)
{
    private Guid _owner;
    private Guid _trip;
    private Guid _viewer;
    private Guid _member;
    private List<Guid> _people = [];

    private static TripMoneyCards Cards(ApplicationDbContext ctx) => new(ctx, new TripLedger(ctx, NullLogger<TripLedger>.Instance));

    private static TripInviteService Invites(ApplicationDbContext ctx) =>
        new(ctx, new NoticeWriter(ctx), new RecordingLiveHints(), TimeProvider.System, NullLogger<TripInviteService>.Instance);

    private static TripMemberService Members(ApplicationDbContext ctx) =>
        new(ctx, new TripAccess(ctx), Cards(ctx), new NoticeWriter(ctx), new RecordingLiveHints(), TimeProvider.System, NullLogger<TripMemberService>.Instance);

    private static TripViewerReader Viewer(ApplicationDbContext ctx) => new(ctx, new TripAccess(ctx), Cards(ctx), TimeProvider.System);

    private static TripSharedListReader Shared(ApplicationDbContext ctx) => new(ctx, Cards(ctx), TimeProvider.System);

    private static TripSharingHooks Hooks(ApplicationDbContext ctx) =>
        new(ctx, Cards(ctx), new NoticeWriter(ctx), new RecordingLiveHints(), TimeProvider.System, NullLogger<TripSharingHooks>.Instance);

    // Owner + up to 10 joined viewers + rows expenses (row count drives the large run), one waiting invite.
    private async Task SeedSharedTripAsync(ApplicationDbContext ctx, int rows)
    {
        var people = await BuddySeed.UsersAsync(ctx, Math.Min(rows, 10) + 2);
        _owner = people[0];
        _people = people;
        _trip = await BuddySeed.TripAsync(ctx, _owner);
        var viewers = people.Skip(1).Take(people.Count - 2).ToList();
        _viewer = viewers[0];
        await BuddySeed.BuddiesAsync(ctx, _owner, people.Skip(1));
        await BuddySeed.MembersAsync(ctx, _trip, _owner, viewers);
        _member = (await BuddySeed.MembersAsync(ctx, _trip, _owner, [people[^1]], TripMemberStatuses.Invited))[0];
        await BuddySeed.ExpensesAsync(ctx, _trip, _owner, rows, [_owner, .. viewers]);
    }

    // The viewer is on `rows` shared trips (page of 20 at most).
    private async Task SeedManyTripsAsync(ApplicationDbContext ctx, int rows)
    {
        var people = await BuddySeed.UsersAsync(ctx, 2);
        _owner = people[0];
        _viewer = people[1];
        for (var i = 0; i < rows; i++) // batch-loop
        {
            var trip = await BuddySeed.TripAsync(ctx, _owner, title: $"Trip {i}"); // batch-loop
            await BuddySeed.MembersAsync(ctx, trip, _owner, [_viewer]); // batch-loop
            _trip = trip;
        }
    }

    [IntegrationFact]
    public Task S1_Members_QueryCount_IsFixed() => QueryAssert.SameCountAsync(Db, SeedSharedTripAsync,
        async ctx => Assert.True((await Members(ctx).GetMembersAsync(_trip, _owner)).Ok), budget: 2);

    [IntegrationFact]
    public Task S7_Preview_QueryCount_IsFixed() => QueryAssert.SameCountAsync(Db, SeedSharedTripAsync,
        async ctx => Assert.True((await Invites(ctx).PreviewAsync(_member, _people[^1])).Ok), budget: 2);

    [IntegrationFact]
    public Task S14_View_QueryCount_IsFixed() => QueryAssert.SameCountAsync(Db, SeedSharedTripAsync,
        async ctx => Assert.True((await Viewer(ctx).GetAsync(_trip, _viewer, null)).Ok), budget: 7);

    [IntegrationFact]
    public Task S15_Shared_QueryCount_IsFixed() => QueryAssert.SameCountAsync(Db, SeedManyTripsAsync,
        async ctx => Assert.NotEmpty((await Shared(ctx).GetAsync(_viewer, SharedListStates.Upcoming, null, 20)).Items), budget: 12);

    [IntegrationFact]
    public Task S16_ExpenseCount_QueryCount_IsFixed() => QueryAssert.SameCountAsync(Db, SeedSharedTripAsync,
        async ctx => Assert.True((await Members(ctx).ExpenseCountAsync(_trip, _owner, _viewer)).Ok), budget: 2);

    [IntegrationFact]
    public Task S17_InviteOptions_QueryCount_IsFixed() => QueryAssert.SameCountAsync(Db, SeedManyTripsAsync,
        async ctx => Assert.True((await Members(ctx).InviteOptionsAsync(_owner, _viewer)).Ok), budget: 2);

    [IntegrationFact]
    public Task TripDto_Sharing_QueryCount_IsFixed() => QueryAssert.SameCountAsync(Db, SeedSharedTripAsync,
        async ctx => Assert.NotNull(await Hooks(ctx).GetSharingAsync(_trip, _owner)), budget: 3);

    [IntegrationFact]
    public async Task S2_Invite_write_count_is_the_same_for_1_and_10_people()
    {
        async Task<int> RunAsync(int n)
        {
            List<Guid> people;
            Guid trip;
            await using (var plain = CreateContext())
            {
                people = await BuddySeed.UsersAsync(plain, n + 1);
                trip = await BuddySeed.TripAsync(plain, people[0]);
                await BuddySeed.BuddiesAsync(plain, people[0], people.Skip(1));
            }
            await using var counting = Db.CreateCountingContext(out var counter);
            counter.Reset();
            var result = await Invites(counting).InviteAsync(trip, people[0], people.Skip(1).ToList());
            Assert.True(result.Ok);
            Assert.All(result.Value!.Results, r => Assert.Equal(InviteOutcomes.Invited, r.Outcome));
            return counter.Count;
        }
        var one = await RunAsync(1);
        var ten = await RunAsync(10);
        Assert.Equal(one, ten);
        Assert.True(ten <= 9, $"S2 used {ten} statements");
    }

    [IntegrationFact]
    public async Task S2_outcomes_NotBuddy_AlreadyOnTrip_AlreadyInvited_and_notice_plus_push()
    {
        await using var ctx = CreateContext();
        var p = await BuddySeed.UsersAsync(ctx, 5);
        var trip = await BuddySeed.TripAsync(ctx, p[0]);
        await BuddySeed.BuddiesAsync(ctx, p[0], [p[1], p[2], p[3]]);
        await BuddySeed.MembersAsync(ctx, trip, p[0], [p[2]]);
        await BuddySeed.MembersAsync(ctx, trip, p[0], [p[3]], TripMemberStatuses.Invited);
        var result = await Invites(ctx).InviteAsync(trip, p[0], [p[1], p[2], p[3], p[4]]);
        Assert.True(result.Ok);
        var outcomes = result.Value!.Results.ToDictionary(r => r.UserId, r => r.Outcome);
        Assert.Equal(InviteOutcomes.Invited, outcomes[p[1]]);
        Assert.Equal(InviteOutcomes.AlreadyOnTrip, outcomes[p[2]]);
        Assert.Equal(InviteOutcomes.AlreadyInvited, outcomes[p[3]]);
        Assert.Equal(InviteOutcomes.NotBuddy, outcomes[p[4]]);
        Assert.Equal(8, result.Value.SeatsLeft);
        await using var check = CreateContext();
        Assert.Equal(1, await check.Set<UserNotice>().CountAsync(n => n.UserId == p[1] && n.TripId == trip && n.Kind == NoticeKinds.TripInvite));
        Assert.False(await check.Set<TripMember>().AnyAsync(m => m.TripId == trip && m.UserId == p[4]));
    }

    [IntegrationFact]
    public async Task S2_blocked_buddy_reads_NotBuddy_and_nothing_is_written()
    {
        await using var ctx = CreateContext();
        var p = await BuddySeed.UsersAsync(ctx, 2);
        var trip = await BuddySeed.TripAsync(ctx, p[0]);
        await BuddySeed.BuddiesAsync(ctx, p[0], [p[1]]);
        ctx.UserBlocks.Add(new UserBlock { Id = Guid.NewGuid(), BlockerId = p[1], BlockedId = p[0], CreatedAt = DateTime.UtcNow });
        await ctx.SaveChangesAsync();
        var result = await Invites(ctx).InviteAsync(trip, p[0], [p[1]]);
        Assert.Equal(InviteOutcomes.NotBuddy, Assert.Single(result.Value!.Results).Outcome);
        Assert.False(await ctx.Set<TripMember>().AnyAsync(m => m.TripId == trip));
    }

    [IntegrationFact]
    public async Task S2_errors_TripNotFound_TripCompleted_NoPlaces_TripFull()
    {
        await using var ctx = CreateContext();
        var p = await BuddySeed.UsersAsync(ctx, 13);
        await BuddySeed.BuddiesAsync(ctx, p[0], p.Skip(1));
        var trip = await BuddySeed.TripAsync(ctx, p[0]);
        Assert.Equal(BuddyErrorTypes.TripNotFound, (await Invites(ctx).InviteAsync(trip, p[1], [p[2]])).ErrorType);

        var past = await BuddySeed.TripAsync(ctx, p[0], start: IstTime.TodayIst(DateTime.UtcNow).AddDays(-10));
        Assert.Equal(BuddyErrorTypes.TripCompleted, (await Invites(ctx).InviteAsync(past, p[0], [p[1]])).ErrorType);

        var empty = await BuddySeed.TripAsync(ctx, p[0]);
        await ctx.Trips.Where(t => t.Id == empty).ExecuteUpdateAsync(s => s.SetProperty(t => t.SpotCount, 0));
        Assert.Equal(BuddyErrorTypes.NoPlaces, (await Invites(ctx).InviteAsync(empty, p[0], [p[1]])).ErrorType);

        await BuddySeed.MembersAsync(ctx, trip, p[0], p.Skip(1).Take(10));
        var full = await Invites(ctx).InviteAsync(trip, p[0], [p[11], p[12]]);
        Assert.Equal(BuddyErrorTypes.TripFull, full.ErrorType);
    }

    [IntegrationFact]
    public async Task S8_two_buddies_racing_for_the_last_seat_one_gets_TripFull()
    {
        Guid trip, a, b, ma, mb;
        await using (var ctx = CreateContext())
        {
            var p = await BuddySeed.UsersAsync(ctx, 13);
            trip = await BuddySeed.TripAsync(ctx, p[0]);
            await BuddySeed.MembersAsync(ctx, trip, p[0], p.Skip(1).Take(10));
            a = p[11];
            b = p[12];
            var invited = await BuddySeed.MembersAsync(ctx, trip, p[0], [a, b], TripMemberStatuses.Invited);
            (ma, mb) = (invited[0], invited[1]);
        }
        await using var c1 = CreateContext();
        await using var c2 = CreateContext();
        var results = await Task.WhenAll(Invites(c1).AcceptAsync(ma, a), Invites(c2).AcceptAsync(mb, b));
        Assert.Single(results, r => r.Ok);
        Assert.Single(results, r => r.ErrorType == BuddyErrorTypes.TripFull);
        await using var check = CreateContext();
        Assert.Equal(11, await check.Set<TripMember>().CountAsync(m => m.TripId == trip && m.Status == TripMemberStatuses.Joined));
    }

    [IntegrationFact]
    public async Task S8_accept_is_idempotent_and_ends_after_cancel_or_a_block()
    {
        await using var ctx = CreateContext();
        var p = await BuddySeed.UsersAsync(ctx, 4);
        var trip = await BuddySeed.TripAsync(ctx, p[0]);
        var m = await BuddySeed.MembersAsync(ctx, trip, p[0], [p[1], p[2], p[3]], TripMemberStatuses.Invited);
        var svc = Invites(ctx);

        Assert.True((await svc.AcceptAsync(m[0], p[1])).Ok);
        var again = await svc.AcceptAsync(m[0], p[1]);
        Assert.Equal((true, trip), (again.Ok, again.Value!.TripId));
        Assert.Equal(BuddyErrorTypes.InviteEnded, (await svc.AcceptAsync(m[0], p[2])).ErrorType);

        await svc.CancelAsync(trip, p[0], p[2]);
        var cancelled = await svc.AcceptAsync(m[1], p[2]);
        Assert.Equal(InviteEndedReasons.Cancelled, Assert.IsType<RentNearBy.Core.DTOs.Buddy.InviteEndedDataDto>(cancelled.ErrorData).Reason);

        ctx.UserBlocks.Add(new UserBlock { Id = Guid.NewGuid(), BlockerId = p[0], BlockedId = p[3], CreatedAt = DateTime.UtcNow });
        await ctx.SaveChangesAsync();
        var blocked = await svc.AcceptAsync(m[2], p[3]);
        Assert.Equal(InviteEndedReasons.Unknown, Assert.IsType<RentNearBy.Core.DTOs.Buddy.InviteEndedDataDto>(blocked.ErrorData).Reason);
    }

    [IntegrationFact]
    public async Task S9_decline_is_silent_and_idempotent_and_blocks_reinvite_for_7_days()
    {
        await using var ctx = CreateContext();
        var p = await BuddySeed.UsersAsync(ctx, 2);
        var trip = await BuddySeed.TripAsync(ctx, p[0]);
        await BuddySeed.BuddiesAsync(ctx, p[0], [p[1]]);
        var invited = await Invites(ctx).InviteAsync(trip, p[0], [p[1]]);
        var memberId = invited.Value!.Results[0].MemberId!.Value;
        Assert.True((await Invites(ctx).DeclineAsync(memberId, p[1])).Ok);
        Assert.True((await Invites(ctx).DeclineAsync(memberId, p[1])).Ok);
        Assert.False(await ctx.Set<UserNotice>().AnyAsync(n => n.UserId == p[1] && n.Kind == NoticeKinds.TripInvite));
        var again = await Invites(ctx).InviteAsync(trip, p[0], [p[1]]);
        Assert.Equal(InviteOutcomes.AlreadyInvited, again.Value!.Results[0].Outcome);
    }

    [IntegrationFact]
    public async Task S9_decline_leaves_the_owner_view_unchanged_and_cancel_removes_the_row()
    {
        await using var ctx = CreateContext();
        var p = await BuddySeed.UsersAsync(ctx, 2);
        var trip = await BuddySeed.TripAsync(ctx, p[0]);
        await BuddySeed.BuddiesAsync(ctx, p[0], [p[1]]);
        var memberId = (await Invites(ctx).InviteAsync(trip, p[0], [p[1]])).Value!.Results[0].MemberId!.Value;
        var before = (await Members(ctx).GetMembersAsync(trip, p[0])).Value!;

        Assert.True((await Invites(ctx).DeclineAsync(memberId, p[1])).Ok);
        var after = (await Members(ctx).GetMembersAsync(trip, p[0])).Value!;
        Assert.Equal(before.PendingInvites, after.PendingInvites);
        Assert.Equal(before.SeatsLeft, after.SeatsLeft);
        Assert.Equal(p[1], Assert.Single(after.Invited).UserId);

        Assert.True((await Invites(ctx).CancelAsync(trip, p[0], p[1])).Ok);
        Assert.Empty((await Members(ctx).GetMembersAsync(trip, p[0])).Value!.Invited);
    }

    [IntegrationFact]
    public async Task S12_remove_then_remove_again_is_NotOnTrip_and_the_view_answers_403_with_hasShare()
    {
        await using var ctx = CreateContext();
        await SeedSharedTripAsync(ctx, 3);
        var removed = await Members(ctx).RemoveAsync(_trip, _owner, _viewer);
        Assert.True(removed.Ok);
        Assert.Equal(3, removed.Value!.SharesExpenses);
        Assert.Equal(BuddyErrorTypes.NotOnTrip, (await Members(ctx).RemoveAsync(_trip, _owner, _viewer)).ErrorType);
        Assert.Equal(1, await ctx.Set<UserNotice>().CountAsync(n => n.UserId == _viewer && n.Kind == NoticeKinds.TripRemoved));
        var view = await Viewer(ctx).GetAsync(_trip, _viewer, null);
        Assert.Equal((BuddyErrorTypes.NotOnTrip, (int?)403), (view.ErrorType, view.Status));
        Assert.Equal(BuddyErrorTypes.TripNotFound, (await Members(ctx).GetMembersAsync(_trip, _viewer)).ErrorType);
    }

    [IntegrationFact]
    public async Task S13_leave_is_idempotent_and_writes_no_notice()
    {
        await using var ctx = CreateContext();
        await SeedSharedTripAsync(ctx, 3);
        var left = await Members(ctx).LeaveAsync(_trip, _viewer);
        Assert.True(left.Ok);
        Assert.True((await Members(ctx).LeaveAsync(_trip, _viewer)).Ok);
        Assert.Equal(BuddyErrorTypes.TripNotFound, (await Members(ctx).LeaveAsync(_trip, _owner)).ErrorType);
        Assert.False(await ctx.Set<UserNotice>().AnyAsync(n => n.TripId == _trip && n.UserId == _owner));
    }

    [IntegrationFact]
    public async Task S14_stranger_and_invited_get_404_and_a_matching_etag_gets_304()
    {
        await using var ctx = CreateContext();
        await SeedSharedTripAsync(ctx, 3);
        var stranger = (await BuddySeed.UsersAsync(ctx, 1))[0];
        Assert.Equal(BuddyErrorTypes.TripNotFound, (await Viewer(ctx).GetAsync(_trip, stranger, null)).ErrorType);
        Assert.Equal(BuddyErrorTypes.TripNotFound, (await Viewer(ctx).GetAsync(_trip, _people[^1], null)).ErrorType);
        var first = await Viewer(ctx).GetAsync(_trip, _viewer, null);
        Assert.Equal(TripSharingRoles.Viewer, first.Value!.View!.Role);
        var second = await Viewer(ctx).GetAsync(_trip, _viewer, first.Value.ETag);
        Assert.True(second.Value!.NotModified);
        await BuddySeed.PaymentAsync(ctx, _trip, _viewer, _owner, 10);
        await new TripLedger(ctx, NullLogger<TripLedger>.Instance).RefreshStateAsync(_trip, _people, null);
        Assert.False((await Viewer(ctx).GetAsync(_trip, _viewer, first.Value.ETag)).Value!.NotModified);
    }

    [IntegrationFact]
    public async Task S17_options_and_PersonNotFound_for_a_non_buddy()
    {
        await using var ctx = CreateContext();
        await SeedSharedTripAsync(ctx, 3);
        var stranger = (await BuddySeed.UsersAsync(ctx, 1))[0];
        Assert.Equal(BuddyErrorTypes.PersonNotFound, (await Members(ctx).InviteOptionsAsync(_owner, stranger)).ErrorType);
        var options = await Members(ctx).InviteOptionsAsync(_owner, _viewer);
        Assert.Equal(InviteOptions.OnTrip, Assert.Single(options.Value!).Option);
        var invitee = await Members(ctx).InviteOptionsAsync(_owner, _people[^1]);
        Assert.Equal(InviteOptions.InviteSent, Assert.Single(invitee.Value!).Option);
    }

    [IntegrationFact]
    public async Task Block_participant_cancels_waiting_invites_both_ways_and_keeps_joined()
    {
        await using var ctx = CreateContext();
        await SeedSharedTripAsync(ctx, 3);
        var invitee = _people[^1];
        await using (var tx = await ctx.Database.BeginTransactionAsync())
        {
            await new TripInviteBlockParticipant(ctx).OnBlockedAsync(invitee, _owner);
            await new TripInviteBlockParticipant(ctx).OnBlockedAsync(_viewer, _owner);
            await tx.CommitAsync();
        }
        await using var check = CreateContext();
        Assert.Equal(TripMemberStatuses.Cancelled, (await check.Set<TripMember>().SingleAsync(m => m.Id == _member)).Status);
        Assert.Equal(TripMemberStatuses.Joined, (await check.Set<TripMember>().SingleAsync(m => m.TripId == _trip && m.UserId == _viewer)).Status);
    }

    [IntegrationFact]
    public async Task Expiry_job_expires_overdue_invites_only()
    {
        await using var ctx = CreateContext();
        await SeedSharedTripAsync(ctx, 3);
        await ctx.Set<TripMember>().Where(m => m.Id == _member).ExecuteUpdateAsync(s => s.SetProperty(m => m.InviteExpiresAt, DateTime.UtcNow.AddMinutes(-1)));
        Assert.True(await new TripInviteExpiryJob(ctx).RunAsync(default) >= 1);
        await using var check = CreateContext();
        Assert.Equal(TripMemberStatuses.Expired, (await check.Set<TripMember>().SingleAsync(m => m.Id == _member)).Status);
        Assert.Equal(TripMemberStatuses.Joined, (await check.Set<TripMember>().SingleAsync(m => m.TripId == _trip && m.UserId == _viewer)).Status);
    }

    [IntegrationFact]
    public async Task Trip_deleting_hook_tells_joined_viewers_and_keeps_only_TripDeleted_notices()
    {
        await using var ctx = CreateContext();
        await SeedSharedTripAsync(ctx, 3);
        await BuddySeed.NoticesAsync(ctx, _people[^1], [(NoticeKinds.TripInvite, _trip)]);
        int told;
        await using (var tx = await ctx.Database.BeginTransactionAsync())
        {
            told = await Hooks(ctx).OnTripDeletingAsync(_trip, _owner);
            await tx.CommitAsync();
        }
        Assert.Equal(_people.Count - 2, told);
        await using var check = CreateContext();
        Assert.False(await check.Set<UserNotice>().AnyAsync(n => n.TripId == _trip && n.Kind != NoticeKinds.TripDeleted));
        Assert.Equal(told, await check.Set<UserNotice>().CountAsync(n => n.TripId == _trip && n.Kind == NoticeKinds.TripDeleted));
        Assert.Equal(told, await check.OutboxMessages.CountAsync(o => o.Key.StartsWith("tripdel:" + _trip.ToString("N"))));
    }

    [IntegrationFact]
    public async Task Plan_saved_hook_writes_one_delayed_push_and_one_in_place_row_per_viewer()
    {
        await using var ctx = CreateContext();
        await SeedSharedTripAsync(ctx, 3);
        await Hooks(ctx).OnPlanSavedAsync(_trip, _owner, 2);
        await Hooks(ctx).OnPlanSavedAsync(_trip, _owner, 3);
        await using var check = CreateContext();
        var rows = await check.OutboxMessages.Where(o => o.Key.StartsWith("planupd:" + _trip.ToString("N"))).ToListAsync();
        Assert.Equal(_people.Count - 2, rows.Count);
        var notices = await check.Set<UserNotice>().Where(n => n.TripId == _trip && n.Kind == NoticeKinds.PlanUpdated).ToListAsync();
        Assert.Equal(_people.Count - 2, notices.Count);
        Assert.All(notices, n => { Assert.Equal(2, n.Count); Assert.Equal(3, n.Amount); Assert.Equal(_owner, n.ActorUserId); });
        Assert.DoesNotContain(notices, n => n.UserId == _owner);
    }

    [IntegrationFact]
    public async Task Join_upserts_one_TripJoined_row_for_the_owner_in_place()
    {
        await using var ctx = CreateContext();
        await SeedSharedTripAsync(ctx, 3);
        var joined = await Invites(ctx).AcceptAsync(_member, _people[^1]);
        Assert.True(joined.Ok);
        await using var check = CreateContext();
        var row = Assert.Single(await check.Set<UserNotice>().Where(n => n.TripId == _trip && n.Kind == NoticeKinds.TripJoined).ToListAsync());
        Assert.Equal(_owner, row.UserId);
        Assert.Equal(_people[^1], row.ActorUserId);
        Assert.Equal(1, row.Count);
    }

    [IntegrationFact]
    public async Task Remove_deletes_the_removed_viewers_plan_and_money_rows()
    {
        await using var ctx = CreateContext();
        await SeedSharedTripAsync(ctx, 3);
        await BuddySeed.NoticesAsync(ctx, _viewer, [(NoticeKinds.PlanUpdated, _trip), (NoticeKinds.ExpenseAdded, _trip), (NoticeKinds.ShareChanged, _trip)]);
        Assert.True((await Members(ctx).RemoveAsync(_trip, _owner, _viewer)).Ok);
        await using var check = CreateContext();
        var kinds = await check.Set<UserNotice>().Where(n => n.UserId == _viewer && n.TripId == _trip).Select(n => n.Kind).ToListAsync();
        Assert.Equal([NoticeKinds.TripRemoved], kinds);
    }
}
