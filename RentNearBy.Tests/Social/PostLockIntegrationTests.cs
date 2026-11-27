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

// 72-hour post lock + "remove the others first" delete rule (owner 2026-11-27, Buddy_resume/post_notify/SPEC.md use cases 39-44)
// end to end: the owner delete route (TripHandlers.Delete), the owner unpublish, edit, members, pending requests and exemptions.
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

    // Moves the lock start back (the feed time PublishedAt is never touched).
    private async Task AgeLockAsync(Guid tripId, TimeSpan age)
    {
        await using var ctx = CreateContext();
        await ctx.Trips.Where(t => t.Id == tripId).ExecuteUpdateAsync(s => s.SetProperty(t => t.LastPublishedAt, DateTime.UtcNow - age));
    }

    private Task PassLockAsync(Guid tripId) => AgeLockAsync(tripId, PostLockRules.Lock + TimeSpan.FromMinutes(1));

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

    // ------------------------------------------------------------------ 39, 40, 41, 42

    [IntegrationFact]
    public async Task Delete_inside_72_hours_is_refused_with_unlocks_at_and_the_post_stays()
    {
        var owner = await SeekerAsync();
        var trip = await PostAsync(owner);
        var stored = await TripAsync(trip);
        Assert.NotNull(stored.LastPublishedAt);

        var (status, type, data) = Refusal(await DeleteAsync(trip, owner));
        Assert.Equal((409, SocialErrorTypes.PostLocked), (status, type));
        var unlocksAt = data.GetProperty("unlocksAt").GetDateTime().ToUniversalTime();
        Assert.Equal(stored.LastPublishedAt!.Value.ToUniversalTime() + PostLockRules.Lock, unlocksAt, TimeSpan.FromSeconds(1));
        Assert.True(await ExistsAsync(trip));
        Assert.True((await TripAsync(trip)).IsPublic);
    }

    [IntegrationFact]
    public async Task Make_private_inside_72_hours_is_refused_and_the_post_stays_public()
    {
        var owner = await SeekerAsync();
        var trip = await PostAsync(owner);
        await AgeLockAsync(trip, PostLockRules.Lock - TimeSpan.FromMinutes(1));   // 71:59 after posting: still locked
        await using (var ctx = CreateContext())
        {
            var refused = await Posts(ctx).UnpublishAsync(owner, trip);
            Assert.Equal(SocialErrorTypes.PostLocked, refused.ErrorType);
            Assert.NotNull(JsonSerializer.SerializeToElement(refused.ErrorData, Web).GetProperty("unlocksAt").GetDateTime());
            Assert.Equal(SocialErrorTypes.TripNotFound, (await Posts(ctx).UnpublishAsync(await SeekerAsync(), trip)).ErrorType); // not his
        }
        Assert.True((await TripAsync(trip)).IsPublic);
    }

    [IntegrationFact]
    public async Task Edit_inside_72_hours_works_and_does_not_restart_the_lock()
    {
        var owner = await SeekerAsync();
        var trip = await PostAsync(owner);
        var before = await TripAsync(trip);
        await using (var ctx = CreateContext())
        {
            var edited = await Posts(ctx).PutPostAsync(owner, trip, Edit("New plan: lakes first, then the hills."));
            Assert.True(edited.Ok, edited.ErrorType);
            Assert.Equal(3, edited.Value!.Seats);
            Assert.NotNull(edited.Value.UnlocksAt);                                       // the poster sees when it unlocks
        }
        var after = await TripAsync(trip);
        Assert.Equal(before.LastPublishedAt, after.LastPublishedAt);
        Assert.Equal(before.PublishedAt, after.PublishedAt);
    }

    [IntegrationFact]
    public async Task After_72_hours_make_private_and_delete_work()
    {
        var owner = await SeekerAsync();
        var a = await PostAsync(owner);
        await PassLockAsync(a);
        Assert.Equal(200, StatusOf(await DeleteAsync(a, owner)));
        Assert.False(await ExistsAsync(a));

        var b = await PostAsync(owner);
        await PassLockAsync(b);
        await using (var ctx = CreateContext()) Assert.True((await Posts(ctx).UnpublishAsync(owner, b)).Ok);
        Assert.False((await TripAsync(b)).IsPublic);
    }

    [IntegrationFact]
    public async Task A_re_publish_starts_a_new_72_hour_window()
    {
        var owner = await SeekerAsync();
        var trip = await PostAsync(owner);
        await PassLockAsync(trip);
        await using (var ctx = CreateContext()) Assert.True((await Posts(ctx).UnpublishAsync(owner, trip)).Ok);
        await using (var ctx = CreateContext()) Assert.True((await Posts(ctx).PutPostAsync(owner, trip, Edit())).Ok);   // publish again
        await using (var ctx = CreateContext())
            Assert.Equal(SocialErrorTypes.PostLocked, (await Posts(ctx).UnpublishAsync(owner, trip)).ErrorType);
        Assert.Equal(SocialErrorTypes.PostLocked, Refusal(await DeleteAsync(trip, owner)).Type);
    }

    // ------------------------------------------------------------------ 43

    [IntegrationFact]
    public async Task Delete_needs_the_host_alone_and_removing_or_leaving_during_the_lock_works()
    {
        var owner = await SeekerAsync();
        var trip = await PostAsync(owner);
        var stays = await JoinedAsync(owner, trip);
        var removed = await JoinedAsync(owner, trip);
        var leaver = await JoinedAsync(owner, trip);

        // During the lock: the host removes one, another leaves on his own.
        await using (var ctx = CreateContext()) Assert.True((await Members(ctx).RemoveAsync(trip, owner, removed)).Ok);
        await using (var ctx = CreateContext()) Assert.True((await Members(ctx).LeaveAsync(trip, leaver)).Ok);

        await PassLockAsync(trip);
        var (status, type, data) = Refusal(await DeleteAsync(trip, owner));
        Assert.Equal((409, SocialErrorTypes.RemoveMembersFirst), (status, type));
        Assert.Equal(1, data.GetProperty("count").GetInt32());
        Assert.True(await ExistsAsync(trip));

        await using (var ctx = CreateContext()) Assert.True((await Members(ctx).RemoveAsync(trip, owner, stays)).Ok);
        Assert.Equal(200, StatusOf(await DeleteAsync(trip, owner)));
        Assert.False(await ExistsAsync(trip));
    }

    [IntegrationFact]
    public async Task Make_private_then_delete_cannot_get_round_the_alone_rule()
    {
        var owner = await SeekerAsync();
        var trip = await PostAsync(owner);
        await JoinedAsync(owner, trip);
        await PassLockAsync(trip);
        await using (var ctx = CreateContext()) Assert.True((await Posts(ctx).UnpublishAsync(owner, trip)).Ok);   // members stay
        var (status, type, data) = Refusal(await DeleteAsync(trip, owner));
        Assert.Equal((409, SocialErrorTypes.RemoveMembersFirst, 1), (status, type, data.GetProperty("count").GetInt32()));
        await using var check = CreateContext();
        Assert.Equal(1, await check.Set<TripMember>().CountAsync(m => m.TripId == trip && m.Status == TripMemberStatuses.Joined));
    }

    [IntegrationFact]
    public async Task Waiting_requests_end_declined_with_a_calm_notice_when_the_trip_is_deleted()
    {
        var owner = await SeekerAsync();
        var asker = await SeekerAsync();
        var trip = await PostAsync(owner);
        await using (var ctx = CreateContext())
            Assert.True((await Joins(ctx).SendAsync(asker, trip, new SendJoinRequest { ClientRequestId = Guid.NewGuid() })).Ok);
        await PassLockAsync(trip);                       // a waiting requester is not a member: the host is alone
        Assert.Equal(200, StatusOf(await DeleteAsync(trip, owner)));

        await using var check = CreateContext();
        Assert.False(await check.Set<TripJoinRequest>().AnyAsync(r => r.TripId == trip));
        var notice = await check.Set<UserNotice>().SingleAsync(n => n.UserId == asker && n.Kind == NoticeKinds.JoinDeclined && n.TripId == trip);
        Assert.Equal(owner, notice.ActorUserId);
    }

    // ------------------------------------------------------------------ 44 exemptions

    [IntegrationFact]
    public async Task A_trip_that_is_over_is_deleted_with_members_once_the_72_hours_passed()
    {
        var owner = await SeekerAsync();
        var trip = await PostAsync(owner);
        await JoinedAsync(owner, trip);
        // Completing an undated post does not end it (the owner could do that at any time): still locked.
        await using (var ctx = CreateContext())
            await ctx.Trips.Where(t => t.Id == trip).ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, TripStatuses.Completed));
        Assert.Equal(SocialErrorTypes.PostLocked, Refusal(await DeleteAsync(trip, owner)).Type);
        // The last day passed. Safety review 2: the dates are owner-editable, so inside the 72 hours this is still PostLocked
        // (post -> move the dates into the past -> delete at once is refused) ...
        var today = IstTime.TodayIst(DateTime.UtcNow);
        await using (var ctx = CreateContext())
            await ctx.Trips.Where(t => t.Id == trip).ExecuteUpdateAsync(s => s
                .SetProperty(t => t.StartDate, today.AddDays(-3)).SetProperty(t => t.EndDate, today.AddDays(-1)));
        Assert.Equal(SocialErrorTypes.PostLocked, Refusal(await DeleteAsync(trip, owner)).Type);
        Assert.True(await ExistsAsync(trip));
        // ... and once the 72 hours passed an over trip is deleted with a member still inside (no RemoveMembersFirst).
        await PassLockAsync(trip);
        Assert.Equal(200, StatusOf(await DeleteAsync(trip, owner)));
        Assert.False(await ExistsAsync(trip));
    }

    [IntegrationFact]
    public async Task Admin_removal_inside_the_lock_works_and_a_never_published_trip_is_never_locked()
    {
        var owner = await SeekerAsync();
        var trip = await PostAsync(owner);
        await using (var ctx = CreateContext()) Assert.True((await Posts(ctx).UnpublishAsync(null, trip)).Ok);
        Assert.False((await TripAsync(trip)).IsPublic);

        Guid plain;
        await using (var ctx = CreateContext()) plain = await BuddySeed.TripAsync(ctx, owner, title: "Own plan");
        Assert.Equal(200, StatusOf(await DeleteAsync(plain, owner)));
    }

    // ------------------------------------------------------------------ owner read fields

    [IntegrationFact]
    public async Task Only_the_owner_reads_when_the_post_unlocks()
    {
        var owner = await SeekerAsync();
        var viewer = await SeekerAsync();
        var trip = await PostAsync(owner);
        await using var ctx = CreateContext();
        var reader = new PublicTripReader(ctx, TimeProvider.System);
        Assert.NotNull((await reader.ByIdAsync(owner, trip, default))!.UnlocksAt);
        Assert.Null((await reader.ByIdAsync(viewer, trip, default))!.UnlocksAt);
        await ctx.Trips.Where(t => t.Id == trip)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.LastPublishedAt, DateTime.UtcNow - PostLockRules.Lock - TimeSpan.FromMinutes(1)));
        Assert.Null((await reader.ByIdAsync(owner, trip, default))!.UnlocksAt);
    }
}
