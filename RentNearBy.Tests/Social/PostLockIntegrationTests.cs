using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RentNearBy.Api.Handlers;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.DTOs.Social;
using RentNearBy.Core.Entities.Buddy;
using RentNearBy.Core.Entities.Social;
using RentNearBy.Core.Models;
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
using RentNearBy.Tests.Integration.Journeys;
using RentNearBy.Tests.Support.Buddy;
using RentNearBy.Tests.Support.Social;
using static RentNearBy.Tests.Integration.Journeys.JourneyHarness;

namespace RentNearBy.Tests.Social;

// Owner order 2026-11-28 (I-3): no post lock, and the "remove the others first" delete rule for EVERY trip end to end (over or
// not): the owner delete route (TripHandlers.Delete), edit, members and pending requests.
// Needs RENTNEARBY_TEST_DB (PostGIS); skipped without it.
public class PostLockIntegrationTests(TestDatabase db) : IntegrationTestBase(db)
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static PublicTripService Posts(ApplicationDbContext ctx)
        => new(ctx, new UnitOfWork(ctx), new FakeCityCatalog(), SocialTestSeams.Limits(10, plus: true),
            new PublicTripReader(ctx, TimeProvider.System), SocialTestSeams.AfterCommit(ctx), new NoticeWriter(ctx), TimeProvider.System,
            NullLogger<PublicTripService>.Instance);

    private static JoinRequestService Joins(ApplicationDbContext ctx)
    {
        var ac = SocialTestSeams.AfterCommit(ctx);
        return new JoinRequestService(ctx, SocialTestSeams.Limits(10, plus: true), new NoticeWriter(ctx), new RecordingLiveHints(),
            SocialTestSeams.GroupChat(ctx, ac), new PublicTripReader(ctx, TimeProvider.System), ac, TimeProvider.System,
            NullLogger<JoinRequestService>.Instance);
    }

    private static TripMemberService Members(ApplicationDbContext ctx)
    {
        var ac = SocialTestSeams.AfterCommit(ctx);
        return new TripMemberService(ctx, new TripAccess(ctx), new TripMoneyCards(ctx, new TripLedger(ctx, NullLogger<TripLedger>.Instance)),
            new NoticeWriter(ctx), new RecordingLiveHints(), TimeProvider.System, NullLogger<TripMemberService>.Instance,
            SocialTestSeams.GroupChat(ctx, ac), ac);
    }

    private static CreatePublicTripRequest Post() => new()
    {
        ClientTripId = Guid.NewGuid(), CityKeys = [FakeCityCatalog.Nainital], WhenKind = SocialRules.WhenKindWithin,
        WithinMonths = 2, Days = 3, JoinGender = JoinGenders.Anyone, Seats = 4, Text = "Easy hill walks and good food.",
    };

    private static UpdatePublicPostRequest Edit(string text = "Easy hill walks and good food.") => new()
    {
        WhenKind = SocialRules.WhenKindWithin, WithinMonths = 2, Days = 4, JoinGender = JoinGenders.Anyone, Seats = 3, Text = text,
    };

    private async Task<Guid> SeekerAsync()
    {
        await using var ctx = CreateContext();
        var id = (await BuddySeed.UsersAsync(ctx, 1))[0];
        await SocialSeed.ProfileAsync(ctx, id, SocialGenders.Male);
        await ctx.Users.Where(u => u.Id == id).ExecuteUpdateAsync(s => s.SetProperty(u => u.CreatedAt, DateTime.UtcNow.AddDays(-3)));
        return id;
    }

    private async Task<Guid> PostAsync(Guid owner)
    {
        await using var ctx = CreateContext();
        var created = await Posts(ctx).CreateAsync(owner, Post());
        Assert.True(created.Ok, created.ErrorType);
        return created.Value!.TripId;
    }

    private async Task<Guid> JoinedAsync(Guid owner, Guid tripId)
    {
        var member = await SeekerAsync();
        await using var ctx = CreateContext();
        Assert.True((await Joins(ctx).SendAsync(member, tripId, new SendJoinRequest { ClientRequestId = Guid.NewGuid() })).Ok);
        var requestId = await ctx.Set<TripJoinRequest>().Where(r => r.TripId == tripId && r.RequesterId == member).Select(r => r.Id).SingleAsync();
        var accepted = await Joins(ctx).AcceptAsync(owner, requestId, addToBuddies: false);
        Assert.True(accepted.Ok, accepted.ErrorType);
        return member;
    }

    private async Task<IResult> DeleteAsync(Guid tripId, Guid owner)
    {
        await using var h = new JourneyHarness(Db);
        await using var s = h.NewScope();
        var hooks = new TripSharingHooks(s.Db, new TripMoneyCards(s.Db, new TripLedger(s.Db, NullLogger<TripLedger>.Instance)),
            new NoticeWriter(s.Db), new RecordingLiveHints(), TimeProvider.System, NullLogger<TripSharingHooks>.Instance);
        var guard = new TripMoneyGuard(s.Db, new TripAccess(s.Db), new TripLedger(s.Db, NullLogger<TripLedger>.Instance));
        return await TripHandlers.Delete(tripId, Principal(owner), s.Uow, s.Closure, s.Journeys, s.Db, guard, hooks, new NoticeWriter(s.Db));
    }

    private static (int Status, string? Type, JsonElement Data) Refusal(IResult result)
    {
        var body = (ApiResponse<object>)((IValueHttpResult)result).Value!;
        return (StatusOf(result), body.Error?.Type, JsonSerializer.SerializeToElement(body.Data, Web));
    }

    private async Task<bool> ExistsAsync(Guid tripId)
    {
        await using var ctx = CreateContext();
        return await ctx.Trips.AnyAsync(t => t.Id == tripId);
    }

    private async Task<Trip0> TripAsync(Guid tripId)
    {
        await using var ctx = CreateContext();
        return await ctx.Trips.AsNoTracking().Where(t => t.Id == tripId)
            .Select(t => new Trip0(t.IsPublic, t.PublishedAt, t.LastPublishedAt)).SingleAsync();
    }

    private sealed record Trip0(bool IsPublic, DateTime? PublishedAt, DateTime? LastPublishedAt);

    // ------------------------------------------------------------------ no lock (owner 2026-11-28, I-3)

    [IntegrationFact]
    public async Task Delete_right_after_posting_works_when_the_host_is_alone()
    {
        var owner = await SeekerAsync();
        var trip = await PostAsync(owner);
        Assert.Equal(200, StatusOf(await DeleteAsync(trip, owner)));
        Assert.False(await ExistsAsync(trip));
    }

    [IntegrationFact]
    public async Task Edit_right_after_posting_works_and_carries_no_unlock_time()
    {
        var owner = await SeekerAsync();
        var trip = await PostAsync(owner);
        var before = await TripAsync(trip);
        await using (var ctx = CreateContext())
        {
            var edited = await Posts(ctx).PutPostAsync(owner, trip, Edit("New plan: lakes first, then the hills."));
            Assert.True(edited.Ok, edited.ErrorType);
            Assert.Equal(3, edited.Value!.Seats);
            Assert.Null(edited.Value.UnlocksAt);
        }
        Assert.Equal(before.PublishedAt, (await TripAsync(trip)).PublishedAt);
    }

    // ------------------------------------------------------------------ remove the others first (every trip)

    [IntegrationFact]
    public async Task Delete_of_a_post_needs_the_host_alone_and_removing_or_leaving_works()
    {
        var owner = await SeekerAsync();
        var trip = await PostAsync(owner);
        var stays = await JoinedAsync(owner, trip);
        var removed = await JoinedAsync(owner, trip);
        var leaver = await JoinedAsync(owner, trip);

        await using (var ctx = CreateContext()) Assert.True((await Members(ctx).RemoveAsync(trip, owner, removed)).Ok);
        await using (var ctx = CreateContext()) Assert.True((await Members(ctx).LeaveAsync(trip, leaver)).Ok);

        var (status, type, data) = Refusal(await DeleteAsync(trip, owner));
        Assert.Equal((409, SocialErrorTypes.RemoveMembersFirst), (status, type));
        Assert.Equal(1, data.GetProperty("count").GetInt32());
        Assert.True(await ExistsAsync(trip));

        await using (var ctx = CreateContext()) Assert.True((await Members(ctx).RemoveAsync(trip, owner, stays)).Ok);
        Assert.Equal(200, StatusOf(await DeleteAsync(trip, owner)));
        Assert.False(await ExistsAsync(trip));
    }

    [IntegrationFact]
    public async Task A_normal_trip_with_joined_buddies_needs_them_removed_first_too()
    {
        var owner = await SeekerAsync();
        var buddy = await SeekerAsync();
        Guid plain;
        await using (var ctx = CreateContext())
        {
            plain = await BuddySeed.TripAsync(ctx, owner, title: "Own plan");
            await BuddySeed.MembersAsync(ctx, plain, owner, [buddy]);
        }
        var (status, type, data) = Refusal(await DeleteAsync(plain, owner));
        Assert.Equal((409, SocialErrorTypes.RemoveMembersFirst, 1), (status, type, data.GetProperty("count").GetInt32()));
        Assert.True(await ExistsAsync(plain));

        await using (var ctx = CreateContext()) Assert.True((await Members(ctx).LeaveAsync(plain, buddy)).Ok);
        Assert.Equal(200, StatusOf(await DeleteAsync(plain, owner)));
        Assert.False(await ExistsAsync(plain));
    }

    [IntegrationFact]
    public async Task Waiting_requests_end_declined_with_a_calm_notice_when_the_trip_is_deleted()
    {
        var owner = await SeekerAsync();
        var asker = await SeekerAsync();
        var trip = await PostAsync(owner);
        await using (var ctx = CreateContext())
            Assert.True((await Joins(ctx).SendAsync(asker, trip, new SendJoinRequest { ClientRequestId = Guid.NewGuid() })).Ok);
        Assert.Equal(200, StatusOf(await DeleteAsync(trip, owner)));    // a waiting requester is not a member: the host is alone

        await using var check = CreateContext();
        Assert.False(await check.Set<TripJoinRequest>().AnyAsync(r => r.TripId == trip));
        var notice = await check.Set<UserNotice>().SingleAsync(n => n.UserId == asker && n.Kind == NoticeKinds.JoinDeclined && n.TripId == trip);
        Assert.Equal(owner, notice.ActorUserId);
    }

    // ------------------------------------------------------------------ no exemption: the trip is over

    [IntegrationFact]
    public async Task A_trip_that_is_over_still_needs_its_members_removed_first()
    {
        var owner = await SeekerAsync();
        var trip = await PostAsync(owner);
        var member = await JoinedAsync(owner, trip);
        // Completing an undated post does not end it (the owner could do that at any time).
        await using (var ctx = CreateContext())
            await ctx.Trips.Where(t => t.Id == trip).ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, TripStatuses.Completed));
        Assert.Equal(SocialErrorTypes.RemoveMembersFirst, Refusal(await DeleteAsync(trip, owner)).Type);
        // The last day passed: the trip is over, and still refused while a member is inside.
        var today = IstTime.TodayIst(DateTime.UtcNow);
        await using (var ctx = CreateContext())
            await ctx.Trips.Where(t => t.Id == trip).ExecuteUpdateAsync(s => s
                .SetProperty(t => t.StartDate, today.AddDays(-3)).SetProperty(t => t.EndDate, today.AddDays(-1)));
        Assert.Equal(SocialErrorTypes.RemoveMembersFirst, Refusal(await DeleteAsync(trip, owner)).Type);
        await using (var ctx = CreateContext()) Assert.True((await Members(ctx).LeaveAsync(trip, member)).Ok);
        Assert.Equal(200, StatusOf(await DeleteAsync(trip, owner)));
        Assert.False(await ExistsAsync(trip));
    }

    [IntegrationFact]
    public async Task Admin_removal_works_at_once()
    {
        var owner = await SeekerAsync();
        var trip = await PostAsync(owner);
        await using (var ctx = CreateContext()) Assert.True((await Posts(ctx).UnpublishAsync(null, trip)).Ok);
        Assert.False((await TripAsync(trip)).IsPublic);
    }

    [IntegrationFact]
    public async Task No_reader_gets_an_unlock_time()
    {
        var owner = await SeekerAsync();
        var viewer = await SeekerAsync();
        var trip = await PostAsync(owner);
        await using var ctx = CreateContext();
        var reader = new PublicTripReader(ctx, TimeProvider.System);
        Assert.Null((await reader.ByIdAsync(owner, trip, default))!.UnlocksAt);
        Assert.Null((await reader.ByIdAsync(viewer, trip, default))!.UnlocksAt);
    }
}
