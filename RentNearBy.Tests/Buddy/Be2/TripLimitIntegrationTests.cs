using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RentNearBy.Api.Handlers;
using RentNearBy.Api.Validators;
using RentNearBy.Core.DTOs.Buddy;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
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
using RentNearBy.Tests.Integration.Journeys;
using RentNearBy.Tests.Support.Buddy;
using static RentNearBy.Tests.Integration.Journeys.JourneyHarness;

namespace RentNearBy.Tests.Buddy.Be2;

// CONTRACT_TRIPLIMIT 7. Needs RENTNEARBY_TEST_DB (PostGIS); skipped without it.
public class TripLimitIntegrationTests(TestDatabase db) : IntegrationTestBase(db)
{
    private static TripMoneyCards Cards(ApplicationDbContext ctx) => new(ctx, new TripLedger(ctx, NullLogger<TripLedger>.Instance));

    private static TripInviteService Invites(ApplicationDbContext ctx) =>
        new(ctx, new NoticeWriter(ctx), new RecordingLiveHints(), TimeProvider.System, NullLogger<TripInviteService>.Instance);

    private static TripMemberService Members(ApplicationDbContext ctx) =>
        new(ctx, new TripAccess(ctx), Cards(ctx), new NoticeWriter(ctx), new RecordingLiveHints(), TimeProvider.System, NullLogger<TripMemberService>.Instance);

    private static async Task<IResult> CreateAsync(JourneyHarness h, Guid user, Guid? clientId = null)
    {
        await using var s = h.NewScope();
        return await TripHandlers.Create(new SaveTripRequest { Plan = h.BuildPlan(("Nainital", 210000001, 2)), ClientTripId = clientId },
            new SaveTripRequestValidator(), Principal(user), s.Uow, null!, s.Journeys, h.Loggers, h.RateLimit, new DefaultHttpContext(), s.Db);
    }

    private static async Task<IResult> DeleteAsync(JourneyHarness h, Guid trip, Guid user)
    {
        await using var s = h.NewScope();
        var guard = new TripMoneyGuard(s.Db, new TripAccess(s.Db), new TripLedger(s.Db, NullLogger<TripLedger>.Instance));
        return await TripHandlers.Delete(trip, Principal(user), s.Uow, s.Closure, s.Journeys, s.Db, guard);
    }

    private static (string? Type, TripSlotsDto? Slots) ErrorOf(IResult result)
    {
        var body = (ApiResponse<object>)((IValueHttpResult)result).Value!;
        return (body.Error?.Type, body.Data as TripSlotsDto);
    }

    private async Task<int> CountAsync(Guid user)
    {
        await using var ctx = CreateContext();
        return (await TripSlots.ReadAsync(ctx, user)).Count;
    }

    // User with `owned` own trips and `joined` trips of another owner; returns the other owner.
    private async Task<Guid> FillAsync(Guid user, int owned, int joined)
    {
        await using var ctx = CreateContext();
        var other = (await BuddySeed.UsersAsync(ctx, 1, "Owner"))[0];
        for (var i = 0; i < owned; i++) await BuddySeed.TripAsync(ctx, user, title: $"Own {i}"); // batch-loop
        for (var i = 0; i < joined; i++) // batch-loop
        {
            var t = await BuddySeed.TripAsync(ctx, other, title: $"Joined {i}"); // batch-loop
            await BuddySeed.MembersAsync(ctx, t, other, [user]); // batch-loop
        }
        return other;
    }

    private async Task<(Guid Trip, Guid Member, Guid Owner)> InviteAsync(Guid user)
    {
        await using var ctx = CreateContext();
        var owner = (await BuddySeed.UsersAsync(ctx, 1, "Inviter"))[0];
        var trip = await BuddySeed.TripAsync(ctx, owner, title: "Goa Trip");
        var member = (await BuddySeed.MembersAsync(ctx, trip, owner, [user], TripMemberStatuses.Invited))[0];
        await BuddySeed.NoticesAsync(ctx, user, [(NoticeKinds.TripInvite, trip)]);
        return (trip, member, owner);
    }

    [IntegrationFact]
    public async Task Count_is_owned_plus_joined_only_and_past_trips_count()
    {
        await using var ctx = CreateContext();
        var p = await BuddySeed.UsersAsync(ctx, 2);
        var (me, other) = (p[0], p[1]);
        var past = await BuddySeed.TripAsync(ctx, me, start: IstTime.TodayIst(DateTime.UtcNow).AddDays(-20), title: "Old Manali Trip");
        await ctx.Trips.Where(t => t.Id == past).ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, TripStatuses.Completed));
        await BuddySeed.TripAsync(ctx, me);
        var joined = await BuddySeed.TripAsync(ctx, other, title: "Goa Trip");
        await BuddySeed.MembersAsync(ctx, joined, other, [me]);
        foreach (var status in new[] { TripMemberStatuses.Invited, TripMemberStatuses.Left, TripMemberStatuses.Removed, TripMemberStatuses.Declined })
        {
            var t = await BuddySeed.TripAsync(ctx, other); // batch-loop
            await BuddySeed.MembersAsync(ctx, t, other, [me], status); // batch-loop
        }

        var slots = await TripSlots.ReadAsync(ctx, me);
        Assert.Equal(3, slots.Count);
        Assert.Equal(TripLimits.MaxTripsPerUser, slots.Limit);
        Assert.Equal(past, slots.Trips[0].Id);
        Assert.Equal(2, slots.Trips.Count(t => t.Role == TripSharingRoles.Owner && t.OwnerName == null));
        var viewer = Assert.Single(slots.Trips, t => t.Role == TripSharingRoles.Viewer);
        Assert.Equal(joined, viewer.Id);
        Assert.Equal("Person 2", viewer.OwnerName);
    }

    [IntegrationFact]
    public async Task Create_at_the_limit_is_409_with_the_list_and_inserts_nothing_but_a_known_client_id_replays()
    {
        await using var h = new JourneyHarness(Db);
        var me = await h.AddSeekerAsync();
        await FillAsync(me, 2, 1);
        var known = Guid.NewGuid();
        await using (var ctx = CreateContext())
        {
            var first = await ctx.Trips.Where(t => t.OwnerUserId == me).Select(t => t.Id).FirstAsync();
            await ctx.Trips.Where(t => t.Id == first).ExecuteUpdateAsync(s => s.SetProperty(t => t.ClientTripId, known));
        }

        var refused = await CreateAsync(h, me, Guid.NewGuid());
        Assert.Equal(409, StatusOf(refused));
        var (type, slots) = ErrorOf(refused);
        Assert.Equal(BuddyErrorTypes.TripLimitReached, type);
        Assert.Equal(3, slots!.Limit);
        Assert.Equal(3, slots.Count);
        Assert.Equal(2, slots.Trips.Count(t => t.Role == TripSharingRoles.Owner));
        Assert.Equal(1, slots.Trips.Count(t => t.Role == TripSharingRoles.Viewer));
        Assert.Equal(3, await CountAsync(me));

        Assert.Equal(200, StatusOf(await CreateAsync(h, me, known)));
        Assert.Equal(3, await CountAsync(me));
    }

    [IntegrationFact]
    public async Task Create_below_the_limit_succeeds_and_a_delete_frees_a_slot()
    {
        await using var h = new JourneyHarness(Db);
        var me = await h.AddSeekerAsync();
        await FillAsync(me, 2, 0);
        Assert.Equal(201, StatusOf(await CreateAsync(h, me)));
        Assert.Equal(409, StatusOf(await CreateAsync(h, me)));
        Guid oldest;
        await using (var ctx = CreateContext()) oldest = (await TripSlots.ReadAsync(ctx, me)).Trips[0].Id;
        Assert.Equal(200, StatusOf(await DeleteAsync(h, oldest, me)));
        Assert.Equal(201, StatusOf(await CreateAsync(h, me)));
    }

    [IntegrationFact]
    public async Task Accept_at_the_limit_writes_nothing_then_succeeds_after_delete_leave_or_remove()
    {
        await using var h = new JourneyHarness(Db);
        var me = await h.AddSeekerAsync();
        var other = await FillAsync(me, 1, 2);
        var (trip, member, _) = await InviteAsync(me);
        TripMember before;
        await using (var ctx = CreateContext()) before = await ctx.Set<TripMember>().AsNoTracking().FirstAsync(m => m.Id == member);
        int outboxBefore;
        await using (var ctx = CreateContext()) outboxBefore = await ctx.Set<OutboxMessage>().CountAsync();

        await using (var ctx = CreateContext())
        {
            var refused = await Invites(ctx).AcceptAsync(member, me);
            Assert.Equal(BuddyErrorTypes.TripLimitReached, refused.ErrorType);
            var slots = Assert.IsType<TripSlotsDto>(refused.ErrorData);
            Assert.Equal(3, slots.Count);
        }
        await using (var ctx = CreateContext())
        {
            var after = await ctx.Set<TripMember>().AsNoTracking().FirstAsync(m => m.Id == member);
            Assert.Equal(TripMemberStatuses.Invited, after.Status);
            Assert.Equal(before.InviteExpiresAt, after.InviteExpiresAt);
            Assert.Equal(before.Version, after.Version);
            Assert.True(await ctx.Set<UserNotice>().AnyAsync(n => n.UserId == me && n.TripId == trip && n.Kind == NoticeKinds.TripInvite));
            Assert.Equal(outboxBefore, await ctx.Set<OutboxMessage>().CountAsync());
        }

        await using (var ctx = CreateContext())
        {
            var joinedTrip = (await TripSlots.ReadAsync(ctx, me)).Trips.First(t => t.Role == TripSharingRoles.Viewer).Id;
            Assert.True((await Members(ctx).LeaveAsync(joinedTrip, me)).Ok);
            Assert.True((await Invites(ctx).AcceptAsync(member, me)).Ok);
        }

        // Full again: the other owner removes him from one trip, then a second invite is accepted.
        var (_, second, _) = await InviteAsync(me);
        await using (var ctx = CreateContext())
        {
            Assert.Equal(BuddyErrorTypes.TripLimitReached, (await Invites(ctx).AcceptAsync(second, me)).ErrorType);
            var otherTrip = (await TripSlots.ReadAsync(ctx, me)).Trips.First(t => t.Role == TripSharingRoles.Viewer && t.OwnerName == "Owner 1").Id;
            Assert.True((await Members(ctx).RemoveAsync(otherTrip, other, me)).Ok);
            Assert.True((await Invites(ctx).AcceptAsync(second, me)).Ok);
        }

        // Full again: delete his own trip, then a third invite is accepted.
        var (_, third, _) = await InviteAsync(me);
        await using (var ctx = CreateContext())
            Assert.Equal(BuddyErrorTypes.TripLimitReached, (await Invites(ctx).AcceptAsync(third, me)).ErrorType);
        Guid own;
        await using (var ctx = CreateContext()) own = await ctx.Trips.Where(t => t.OwnerUserId == me).Select(t => t.Id).FirstAsync();
        Assert.Equal(200, StatusOf(await DeleteAsync(h, own, me)));
        await using (var ctx = CreateContext())
            Assert.True((await Invites(ctx).AcceptAsync(third, me)).Ok);
        Assert.Equal(3, await CountAsync(me));
    }

    [IntegrationFact]
    public async Task TripFull_wins_over_the_limit_and_an_already_joined_repeat_is_200()
    {
        await using var ctx = CreateContext();
        var me = (await BuddySeed.UsersAsync(ctx, 1))[0];
        await FillAsync(me, 3, 0);
        var p = await BuddySeed.UsersAsync(ctx, 11);
        var trip = await BuddySeed.TripAsync(ctx, p[0]);
        await BuddySeed.MembersAsync(ctx, trip, p[0], p.Skip(1).Take(10));
        var member = (await BuddySeed.MembersAsync(ctx, trip, p[0], [me], TripMemberStatuses.Invited))[0];
        Assert.Equal(BuddyErrorTypes.TripFull, (await Invites(ctx).AcceptAsync(member, me)).ErrorType);

        var me2 = (await BuddySeed.UsersAsync(ctx, 1))[0];
        var other = await FillAsync(me2, 2, 0);
        var t2 = await BuddySeed.TripAsync(ctx, other);
        var joined = (await BuddySeed.MembersAsync(ctx, t2, other, [me2]))[0];
        Assert.Equal(3, await CountAsync(me2));
        Assert.True((await Invites(ctx).AcceptAsync(joined, me2)).Ok);
    }

    [IntegrationFact]
    public async Task Delete_with_open_payments_at_the_limit_is_still_refused()
    {
        await using var h = new JourneyHarness(Db);
        var me = await h.AddSeekerAsync();
        await FillAsync(me, 2, 0);
        Guid trip, viewer;
        await using (var ctx = CreateContext())
        {
            viewer = (await BuddySeed.UsersAsync(ctx, 1))[0];
            trip = await BuddySeed.TripAsync(ctx, me);
            await BuddySeed.MembersAsync(ctx, trip, me, [viewer]);
            await BuddySeed.ExpensesAsync(ctx, trip, me, 1, [me, viewer]);
        }
        var refused = await DeleteAsync(h, trip, me);
        Assert.Equal(409, StatusOf(refused));
        Assert.Equal(BuddyErrorTypes.TripHasOpenBalances, ErrorOf(refused).Type);
        Assert.Equal(3, await CountAsync(me));
    }

    [IntegrationFact]
    public async Task Race_six_parallel_creates_at_two_trips_insert_exactly_one()
    {
        await using var h = new JourneyHarness(Db);
        var me = await h.AddSeekerAsync();
        await FillAsync(me, 2, 0);
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => CreateAsync(h, me, Guid.NewGuid())));
        Assert.Single(results, r => StatusOf(r) == 201);
        Assert.Equal(5, results.Count(r => StatusOf(r) == 409 && ErrorOf(r).Type == BuddyErrorTypes.TripLimitReached));
        Assert.Equal(3, await CountAsync(me));
    }

    [IntegrationFact]
    public async Task Race_two_parallel_accepts_at_two_trips_join_exactly_one()
    {
        Guid me;
        await using (var ctx = CreateContext()) me = (await BuddySeed.UsersAsync(ctx, 1))[0];
        await FillAsync(me, 2, 0);
        var (_, a, _) = await InviteAsync(me);
        var (_, b, _) = await InviteAsync(me);
        await using var c1 = CreateContext();
        await using var c2 = CreateContext();
        var results = await Task.WhenAll(Invites(c1).AcceptAsync(a, me), Invites(c2).AcceptAsync(b, me));
        Assert.Single(results, r => r.Ok);
        Assert.Single(results, r => r.ErrorType == BuddyErrorTypes.TripLimitReached);
        Assert.Equal(3, await CountAsync(me));
    }

    [IntegrationFact]
    public async Task Race_create_and_accept_in_parallel_stay_at_the_limit()
    {
        await using var h = new JourneyHarness(Db);
        var me = await h.AddSeekerAsync();
        await FillAsync(me, 2, 0);
        var (_, member, _) = await InviteAsync(me);
        await using var c = CreateContext();
        var create = CreateAsync(h, me, Guid.NewGuid());
        var accept = Invites(c).AcceptAsync(member, me);
        await Task.WhenAll(create, accept);
        Assert.True((StatusOf(create.Result) == 201) ^ accept.Result.Ok);
        Assert.Equal(3, await CountAsync(me));
    }

    [IntegrationFact]
    public async Task Race_two_creates_with_the_same_client_id_both_answer_one_row()
    {
        await using var h = new JourneyHarness(Db);
        var me = await h.AddSeekerAsync();
        await FillAsync(me, 2, 0);
        var id = Guid.NewGuid();
        var results = await Task.WhenAll(CreateAsync(h, me, id), CreateAsync(h, me, id));
        Assert.All(results, r => Assert.InRange(StatusOf(r), 200, 201));
        await using var ctx = CreateContext();
        Assert.Equal(1, await ctx.Trips.CountAsync(t => t.OwnerUserId == me && t.ClientTripId == id));
        Assert.Equal(3, await CountAsync(me));
    }

    // Seeds `rows` owned trips (3 or 30): the read is one statement, at most `limit` rows, real total.
    [IntegrationFact]
    public async Task ReadAsync_is_one_statement_with_0_3_and_30_trips()
    {
        foreach (var n in new[] { 0, 3, 30 })
        {
            Guid me;
            await using (var plain = CreateContext())
            {
                me = (await BuddySeed.UsersAsync(plain, 1))[0];
                for (var i = 0; i < n; i++) await BuddySeed.TripAsync(plain, me); // batch-loop
            }
            await using var counting = Db.CreateCountingContext(out var counter);
            counter.Reset();
            var slots = await TripSlots.ReadAsync(counting, me);
            Assert.Equal(1, counter.Count);
            Assert.Equal(n, slots.Count);
            Assert.Equal(Math.Min(n, TripLimits.MaxTripsPerUser), slots.Trips.Count);
        }
    }

    [IntegrationFact]
    public async Task Accept_costs_one_statement_more_refused_or_not()
    {
        async Task<int> RunAsync(int owned)
        {
            Guid me;
            await using (var plain = CreateContext()) me = (await BuddySeed.UsersAsync(plain, 1))[0];
            await FillAsync(me, owned, 0);
            var (_, member, _) = await InviteAsync(me);
            await using var counting = Db.CreateCountingContext(out var counter);
            counter.Reset();
            var result = await Invites(counting).AcceptAsync(member, me);
            Assert.Equal(owned < TripLimits.MaxTripsPerUser, result.Ok);
            return counter.Count;
        }
        var ok = await RunAsync(0);
        var refused = await RunAsync(3);
        // Refused = first read + trip lock + re-read + slot read; success adds the accept write, notices and push.
        Assert.Equal(4, refused);
        Assert.True(ok > refused, $"accept used {ok} statements, refusal {refused}");
    }
}
