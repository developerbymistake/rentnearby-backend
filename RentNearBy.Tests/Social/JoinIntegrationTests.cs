using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RentNearBy.Core.DTOs.Buddy;
using RentNearBy.Core.DTOs.Social;
using RentNearBy.Core.Entities.Buddy;
using RentNearBy.Core.Entities.Social;
using RentNearBy.Core.Interfaces.Social;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Repositories;
using RentNearBy.Infrastructure.Services.Buddy;
using RentNearBy.Infrastructure.Services.Buddy.Be2;
using RentNearBy.Infrastructure.Services.Buddy.Be3;
using RentNearBy.Infrastructure.Services.Buddy.Be4;
using RentNearBy.Infrastructure.Services.Social;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Support.Buddy;
using RentNearBy.Tests.Support.Social;

namespace RentNearBy.Tests.Social;

// Contract 2.5: join requests, accept = one member row (no follow), the monthly cap, the gender rule and the start-date
// lock of request-joined members. Needs RENTNEARBY_TEST_DB (PostGIS); skipped without it.
public class JoinIntegrationTests(TestDatabase db) : IntegrationTestBase(db)
{
    private static PublicTripService Posts(ApplicationDbContext ctx) =>
        new(ctx, new UnitOfWork(ctx), new FakeCityCatalog(), SocialTestSeams.Limits(10, plus: true), new PublicTripReader(ctx, TimeProvider.System),
            SocialTestSeams.AfterCommit(ctx), TimeProvider.System, NullLogger<PublicTripService>.Instance);

    private static JoinRequestService Joins(ApplicationDbContext ctx, ISeekerLimits? limits = null)
    {
        var ac = SocialTestSeams.AfterCommit(ctx);
        return new JoinRequestService(ctx, limits ?? SocialTestSeams.Limits(10), new NoticeWriter(ctx), new RecordingLiveHints(),
            SocialTestSeams.GroupChat(ctx, ac), new PublicTripReader(ctx, TimeProvider.System), ac, TimeProvider.System,
            NullLogger<JoinRequestService>.Instance);
    }

    private static SendJoinRequest Ask(string? note = null) => new() { ClientRequestId = Guid.NewGuid(), Note = note };

    private static CreatePublicTripRequest Post(string joinGender = JoinGenders.Anyone, int seats = 2) => new()
    {
        ClientTripId = Guid.NewGuid(), CityKeys = [FakeCityCatalog.Nainital], WhenKind = SocialRules.WhenKindWithin,
        WithinMonths = 2, Days = 3, JoinGender = joinGender, Seats = seats, Text = "Easy hill walks and good food.",
    };

    private async Task<Guid> SeekerAsync(string gender = SocialGenders.Male)
    {
        await using var ctx = CreateContext();
        var id = (await BuddySeed.UsersAsync(ctx, 1))[0];
        await SocialSeed.ProfileAsync(ctx, id, gender);
        return id;
    }

    private async Task<Guid> PostAsync(Guid owner, string joinGender = JoinGenders.Anyone, int seats = 2)
    {
        await using var ctx = CreateContext();
        var created = await Posts(ctx).CreateAsync(owner, Post(joinGender, seats));
        Assert.True(created.Ok, created.ErrorType);
        return created.Value!.TripId;
    }

    private async Task<Guid> RequestIdAsync(Guid tripId, Guid requester)
    {
        await using var ctx = CreateContext();
        return await ctx.Set<TripJoinRequest>().Where(r => r.TripId == tripId && r.RequesterId == requester).Select(r => r.Id).SingleAsync();
    }

    [IntegrationFact]
    public async Task Free_seeker_sends_one_request_a_month_and_a_second_ask_is_refused()
    {
        var owner1 = await SeekerAsync();
        var owner2 = await SeekerAsync();
        var me = await SeekerAsync();
        var trip1 = await PostAsync(owner1);
        var trip2 = await PostAsync(owner2);
        await using var ctx = CreateContext();
        var free = SocialTestSeams.Limits(10);
        Assert.True((await Joins(ctx, free).SendAsync(me, trip1, Ask())).Ok);
        Assert.Equal(SocialErrorTypes.AlreadyRequested, (await Joins(ctx, free).SendAsync(me, trip1, Ask())).ErrorType);
        Assert.Equal(SocialErrorTypes.JoinRequestLimit, (await Joins(ctx, free).SendAsync(me, trip2, Ask())).ErrorType);
        Assert.True((await Joins(ctx, SocialTestSeams.Limits(10, plus: true)).SendAsync(me, trip2, Ask())).Ok);
    }

    [IntegrationFact]
    public async Task Free_seeker_with_a_full_trip_slot_is_refused_at_send_and_keeps_his_monthly_request()
    {
        var owner = await SeekerAsync();
        var me = await SeekerAsync();
        var trip = await PostAsync(owner);
        await PostAsync(me); // his one own Draft trip fills the free slot
        await using var ctx = CreateContext();
        var refused = await Joins(ctx, SocialTestSeams.Limits(1)).SendAsync(me, trip, Ask());
        Assert.Equal(SocialErrorTypes.TripLimitReached, refused.ErrorType);
        Assert.Equal(409, SocialErrorTypes.StatusOf(SocialErrorTypes.TripLimitReached));
        Assert.Equal(0, await ctx.Set<TripJoinRequest>().CountAsync(r => r.RequesterId == me));
    }

    [IntegrationFact]
    public async Task Same_client_request_id_replays_the_first_answer()
    {
        var owner = await SeekerAsync();
        var me = await SeekerAsync();
        var trip = await PostAsync(owner);
        await using var ctx = CreateContext();
        var ask = Ask("See you there, mail me at a@b.com");
        var first = await Joins(ctx).SendAsync(me, trip, ask);
        var again = await Joins(ctx).SendAsync(me, trip, ask);
        Assert.Equal(first.Value!.Id, again.Value!.Id);
        var note = await ctx.Set<TripJoinRequest>().AsNoTracking().Where(r => r.Id == first.Value.Id).Select(r => r.Note).SingleAsync();
        Assert.DoesNotContain("a@b.com", note);
    }

    [IntegrationFact]
    public async Task Gender_only_post_refuses_the_other_gender()
    {
        var owner = await SeekerAsync(SocialGenders.Female);
        var man = await SeekerAsync(SocialGenders.Male);
        var woman = await SeekerAsync(SocialGenders.Female);
        var trip = await PostAsync(owner, JoinGenders.Women);
        await using var ctx = CreateContext();
        Assert.Equal(SocialErrorTypes.TripNotOpen, (await Joins(ctx).SendAsync(man, trip, Ask())).ErrorType);
        Assert.True((await Joins(ctx).SendAsync(woman, trip, Ask())).Ok);
    }

    [IntegrationFact]
    public async Task Accept_writes_one_member_row_by_request_and_no_follow()
    {
        var owner = await SeekerAsync();
        var me = await SeekerAsync();
        var trip = await PostAsync(owner);
        await using (var ctx = CreateContext())
            Assert.True((await Joins(ctx).SendAsync(me, trip, Ask())).Ok);
        var requestId = await RequestIdAsync(trip, me);
        await using (var ctx = CreateContext())
        {
            var accepted = await Joins(ctx).AcceptAsync(owner, requestId, addToBuddies: true);
            Assert.True(accepted.Ok, accepted.ErrorType);
            Assert.NotEqual(Guid.Empty, accepted.Value!.RoomId);
            Assert.Equal(SocialErrorTypes.JoinRequestGone, (await Joins(ctx).AcceptAsync(owner, requestId, false)).ErrorType);
        }
        await using var check = CreateContext();
        var member = await check.Set<TripMember>().AsNoTracking().SingleAsync(m => m.TripId == trip && m.UserId == me);
        Assert.Equal((TripMemberStatuses.Joined, TripJoinedVia.Request), (member.Status, member.JoinedVia));
        Assert.False(await check.Set<Follow>().AnyAsync(f => (f.FollowerId == me && f.FolloweeId == owner) || (f.FollowerId == owner && f.FolloweeId == me)));
        // addToBuddies counts only for a follower (tier 1): he does not follow the owner, so no buddy row.
        Assert.False(await check.Set<BuddyLink>().AnyAsync(b => b.UserId == owner && b.BuddyUserId == me));
    }

    [IntegrationFact]
    public async Task Accept_at_the_my_buddies_cap_adds_the_member_and_skips_the_buddy_row()
    {
        var owner = await SeekerAsync();
        var me = await SeekerAsync();
        var trip = await PostAsync(owner);
        await using (var ctx = CreateContext())
        {
            var full = await BuddySeed.UsersAsync(ctx, BuddyLimits.MaxBuddies, "Buddy");
            ctx.Set<BuddyLink>().AddRange(full.Select(b => new BuddyLink { UserId = owner, BuddyUserId = b, CreatedAt = DateTime.UtcNow }));
            ctx.Set<Follow>().Add(new Follow { FollowerId = me, FolloweeId = owner, Status = FollowStatuses.Accepted, CreatedAt = DateTime.UtcNow });
            await ctx.SaveChangesAsync();
            Assert.True((await Joins(ctx).SendAsync(me, trip, Ask())).Ok);
        }
        var requestId = await RequestIdAsync(trip, me);
        await using (var ctx = CreateContext())
        {
            var accepted = await Joins(ctx).AcceptAsync(owner, requestId, addToBuddies: true);
            Assert.True(accepted.Ok, accepted.ErrorType);
        }
        await using var check = CreateContext();
        Assert.True(await check.Set<TripMember>().AnyAsync(m => m.TripId == trip && m.UserId == me && m.Status == TripMemberStatuses.Joined));
        Assert.False(await check.Set<BuddyLink>().AnyAsync(b => b.UserId == owner && b.BuddyUserId == me));
        Assert.Equal(BuddyLimits.MaxBuddies, await check.Set<BuddyLink>().CountAsync(b => b.UserId == owner));
    }

    [IntegrationFact]
    public async Task Trip_delete_removes_the_owner_join_request_notices_and_hints_the_requester()
    {
        var owner = await SeekerAsync();
        var me = await SeekerAsync();
        var trip = await PostAsync(owner);
        await using (var ctx = CreateContext())
            Assert.True((await Joins(ctx).SendAsync(me, trip, Ask())).Ok);
        await using (var check = CreateContext())
            Assert.True(await check.Set<UserNotice>().AnyAsync(n => n.UserId == owner && n.Kind == NoticeKinds.JoinRequest));

        var hints = new RecordingLiveHints();
        await using (var ctx = CreateContext())
        {
            var hooks = new TripSharingHooks(ctx, new TripMoneyCards(ctx, new TripLedger(ctx, NullLogger<TripLedger>.Instance)),
                new NoticeWriter(ctx), hints, TimeProvider.System, NullLogger<TripSharingHooks>.Instance);
            await using (var tx = await ctx.Database.BeginTransactionAsync())
            {
                await hooks.OnTripDeletingAsync(trip, owner);
                await ctx.Trips.Where(t => t.Id == trip).ExecuteDeleteAsync();
                await tx.CommitAsync();
            }
            await hooks.AfterTripDeletedAsync();
        }
        await using var after = CreateContext();
        Assert.False(await after.Set<UserNotice>().AnyAsync(n => n.UserId == owner && n.Kind == NoticeKinds.JoinRequest));
        Assert.Contains(hints.SocialChanged, h => h.Scope == SocialLiveScopes.MyJoinRequests && h.UserIds.Contains(me));
    }

    [IntegrationFact]
    public async Task Last_seat_goes_to_one_requester_only()
    {
        var owner = await SeekerAsync();
        var a = await SeekerAsync();
        var b = await SeekerAsync();
        var trip = await PostAsync(owner, seats: 1);
        await using (var ctx = CreateContext())
        {
            Assert.True((await Joins(ctx).SendAsync(a, trip, Ask())).Ok);
            Assert.True((await Joins(ctx).SendAsync(b, trip, Ask())).Ok);
        }
        var ra = await RequestIdAsync(trip, a);
        var rb = await RequestIdAsync(trip, b);
        await using (var ctx = CreateContext())
        {
            Assert.True((await Joins(ctx).AcceptAsync(owner, ra, false)).Ok);
            Assert.Equal(SocialErrorTypes.TripFull, (await Joins(ctx).AcceptAsync(owner, rb, false)).ErrorType);
        }
        await using var check = CreateContext();
        Assert.Equal(1, await check.Set<TripMember>().CountAsync(m => m.TripId == trip && m.Status == TripMemberStatuses.Joined));
    }

    [IntegrationFact]
    public async Task Cancel_before_accept_leaves_nothing_to_accept()
    {
        var owner = await SeekerAsync();
        var me = await SeekerAsync();
        var trip = await PostAsync(owner);
        await using (var ctx = CreateContext())
            Assert.True((await Joins(ctx).SendAsync(me, trip, Ask())).Ok);
        var requestId = await RequestIdAsync(trip, me);
        await using (var ctx = CreateContext())
        {
            await Joins(ctx).CancelMineAsync(me, trip);
            Assert.Equal(SocialErrorTypes.JoinRequestGone, (await Joins(ctx).AcceptAsync(owner, requestId, false)).ErrorType);
        }
        await using var check = CreateContext();
        Assert.False(await check.Set<TripMember>().AnyAsync(m => m.TripId == trip && m.UserId == me));
    }

    [IntegrationFact]
    public async Task Request_joined_member_has_no_money_until_the_start_date()
    {
        var owner = await SeekerAsync();
        var me = await SeekerAsync();
        var trip = await PostAsync(owner);
        await using (var ctx = CreateContext())
            Assert.True((await Joins(ctx).SendAsync(me, trip, Ask())).Ok);
        var requestId = await RequestIdAsync(trip, me);
        await using (var ctx = CreateContext())
            Assert.True((await Joins(ctx).AcceptAsync(owner, requestId, false)).Ok);

        await using (var ctx = CreateContext())
        {
            var access = new TripAccess(ctx);
            var ledger = new TripLedger(ctx, NullLogger<TripLedger>.Instance);
            Assert.True((await access.GetAsync(trip, me))!.DetailsLocked);
            Assert.False((await access.GetAsync(trip, owner))!.DetailsLocked);
            var balances = await new TripBalanceReader(ctx, access, ledger, TimeProvider.System).BalancesAsync(trip, me);
            Assert.Equal(BuddyErrorTypes.OpensAtStart, balances.Error);
            var pay = await new TripPaymentService(ctx, access, ledger, new NoticeWriter(ctx), new NoticeReader(ctx), new RecordingLiveHints(),
                TimeProvider.System, NullLogger<TripPaymentService>.Instance).SaidPaidAsync(trip, me,
                new SaidPaidRequestDto { OpId = Guid.NewGuid(), ToUserId = owner, Amount = 100 });
            Assert.Equal(BuddyErrorTypes.OpensAtStart, pay.Error);
        }

        await using (var ctx = CreateContext())
            await ctx.Trips.Where(t => t.Id == trip).ExecuteUpdateAsync(s => s.SetProperty(t => t.StartDate, (DateOnly?)IstTime.TodayIst(DateTime.UtcNow)));
        await using var after = CreateContext();
        Assert.False((await new TripAccess(after).GetAsync(trip, me))!.DetailsLocked);
    }
}
