using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RentNearBy.Api.Handlers;
using RentNearBy.Api.Validators;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Social;
using RentNearBy.Core.Interfaces.Social;
using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Repositories;
using RentNearBy.Infrastructure.Services.Social;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Integration.Journeys;
using RentNearBy.Tests.Support.Buddy;
using RentNearBy.Tests.Support.Social;
using static RentNearBy.Tests.Integration.Journeys.JourneyHarness;

namespace RentNearBy.Tests.Social;

// Contract 2.4. Needs RENTNEARBY_TEST_DB (PostGIS); skipped without it.
public class PublicTripIntegrationTests(TestDatabase db) : IntegrationTestBase(db)
{
    private static PublicTripService Posts(ApplicationDbContext ctx, ISeekerLimits limits) =>
        new(ctx, new UnitOfWork(ctx), new FakeCityCatalog(), limits, new PublicTripReader(ctx, TimeProvider.System),
            SocialTestSeams.AfterCommit(ctx), new RentNearBy.Infrastructure.Services.Buddy.Be4.NoticeWriter(ctx), TimeProvider.System,
            NullLogger<PublicTripService>.Instance);

    private static CreatePublicTripRequest Post(string joinGender = JoinGenders.Anyone) => new()
    {
        ClientTripId = Guid.NewGuid(), CityKeys = [FakeCityCatalog.Nainital, FakeCityCatalog.Almora], WhenKind = SocialRules.WhenKindWithin,
        WithinMonths = 2, Days = 4, JoinGender = joinGender, Seats = 2, Text = "Looking for two easy-going trekkers. Call 9876543210",
    };

    private async Task<Guid> SeekerAsync(string gender)
    {
        await using var ctx = CreateContext();
        var id = (await BuddySeed.UsersAsync(ctx, 1))[0];
        await SocialSeed.ProfileAsync(ctx, id, gender);
        return id;
    }

    [IntegrationFact]
    public async Task Post_creates_openable_draft()
    {
        var owner = await SeekerAsync(SocialGenders.Male);
        Guid tripId;
        await using (var ctx = CreateContext())
        {
            var created = await Posts(ctx, SocialTestSeams.Limits(10, plus: true)).CreateAsync(owner, Post());
            Assert.True(created.Ok, created.ErrorType);
            tripId = created.Value!.TripId;
            Assert.DoesNotContain("9876543210", created.Value.Text);
            Assert.Equal(["Nainital", "Almora"], created.Value.CityNames);
        }

        await using var h = new JourneyHarness(Db);
        await using var s = h.NewScope();
        var trip = await s.Db.Trips.AsNoTracking().SingleAsync(t => t.Id == tripId);
        Assert.Equal((TripStatuses.Draft, 0, true), (trip.Status, trip.DayCount, trip.IsPublic));

        var read = await TripHandlers.GetById(tripId, Principal(owner), s.Uow, s.Journeys, s.Db);
        Assert.Equal(200, StatusOf(read));

        var plan = TripPlanReader.Read(trip);
        var save = new SaveTripRequest { Plan = plan, BaseUpdatedAt = trip.UpdatedAt };
        var saved = await TripHandlers.Update(tripId, save, new SaveTripRequestValidator(), Principal(owner), s.Uow, s.Db, h.Chat, h.Notifier,
            h.Loggers, s.Journeys, new FakeCityCatalog());
        Assert.Equal(200, StatusOf(saved));
    }

    [IntegrationFact]
    public async Task Free_seeker_has_one_public_trip()
    {
        var owner = await SeekerAsync(SocialGenders.Female);
        await using var ctx = CreateContext();
        var posts = Posts(ctx, SocialTestSeams.Limits(10));
        Assert.True((await posts.CreateAsync(owner, Post())).Ok);
        var second = await posts.CreateAsync(owner, Post());
        Assert.Equal(SocialErrorTypes.PublicTripLimit, second.ErrorType);
    }

    [IntegrationFact]
    public async Task Women_only_needs_plus_and_every_viewer_sees_it()
    {
        var woman = await SeekerAsync(SocialGenders.Female);
        var man = await SeekerAsync(SocialGenders.Male);
        var otherWoman = await SeekerAsync(SocialGenders.Female);
        Guid tripId;
        await using (var ctx = CreateContext())
        {
            Assert.Equal(SocialErrorTypes.PlusRequired, (await Posts(ctx, SocialTestSeams.Limits(10)).CreateAsync(woman, Post(JoinGenders.Women))).ErrorType);
            var created = await Posts(ctx, SocialTestSeams.Limits(10, plus: true)).CreateAsync(woman, Post(JoinGenders.Women));
            Assert.True(created.Ok, created.ErrorType);
            tripId = created.Value!.TripId;
        }
        await using var read = CreateContext();
        var reader = new PublicTripReader(read, TimeProvider.System);
        var forMan = Assert.Single((await reader.FeedAsync(man, null, 20, null, null, null, default)).Items, p => p.TripId == tripId);
        Assert.True(forMan.NotOpen);
        Assert.NotEqual(string.Empty, forMan.Text);
        var forWoman = Assert.Single((await reader.FeedAsync(otherWoman, null, 20, null, null, null, default)).Items, p => p.TripId == tripId);
        Assert.False(forWoman.NotOpen);
        Assert.Contains(await reader.OfOwnerAsync(man, woman, default), p => p.TripId == tripId);
        var byId = await reader.ByIdAsync(man, tripId, default);
        Assert.True(byId!.NotOpen);
        Assert.NotEqual(string.Empty, byId.Text);
        Assert.True(byId.Seats > 0);
    }

    [IntegrationFact]
    public async Task Any_poster_may_pick_men_only_for_free()
    {
        var woman = await SeekerAsync(SocialGenders.Female);
        await using var ctx = CreateContext();
        var created = await Posts(ctx, SocialTestSeams.Limits(10)).CreateAsync(woman, Post(JoinGenders.Men));
        Assert.True(created.Ok, created.ErrorType);
        Assert.Equal(JoinGenders.Men, created.Value!.JoinGender);
    }

    [IntegrationFact]
    public async Task Feed_who_filter_keeps_only_that_join_rule()
    {
        var poster = await SeekerAsync(SocialGenders.Male);
        var viewer = await SeekerAsync(SocialGenders.Male);
        Guid men;
        await using (var ctx = CreateContext())
        {
            var created = await Posts(ctx, SocialTestSeams.Limits(10, plus: true)).CreateAsync(poster, Post(JoinGenders.Men));
            Assert.True(created.Ok, created.ErrorType);
            men = created.Value!.TripId;
        }
        await using var read = CreateContext();
        var reader = new PublicTripReader(read, TimeProvider.System);
        Assert.Contains((await reader.FeedAsync(viewer, null, 20, null, JoinGenders.Men, null, default)).Items, p => p.TripId == men);
        Assert.DoesNotContain((await reader.FeedAsync(viewer, null, 20, null, JoinGenders.Women, null, default)).Items, p => p.TripId == men);
        Assert.DoesNotContain((await reader.FeedAsync(viewer, null, 20, null, JoinGenders.Anyone, null, default)).Items, p => p.TripId == men);
    }

    [IntegrationFact]
    public async Task Post_json_carries_only_the_month_projection()
    {
        var owner = await SeekerAsync(SocialGenders.Male);
        var viewer = await SeekerAsync(SocialGenders.Male);
        Guid tripId;
        await using (var ctx = CreateContext())
            tripId = (await Posts(ctx, SocialTestSeams.Limits(10)).CreateAsync(owner, Post())).Value!.TripId;
        await using var read = CreateContext();
        var dto = await new PublicTripReader(read, TimeProvider.System).ByIdAsync(viewer, tripId, default);
        var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain("startDate", json);
        Assert.DoesNotContain("endDate", json);
        Assert.Contains("\"kind\":\"within\"", json);
    }

    [IntegrationFact]
    public async Task Unpublish_expires_waiting_requests()
    {
        var owner = await SeekerAsync(SocialGenders.Male);
        var asker = await SeekerAsync(SocialGenders.Male);
        Guid tripId;
        await using (var ctx = CreateContext())
            tripId = (await Posts(ctx, SocialTestSeams.Limits(10)).CreateAsync(owner, Post())).Value!.TripId;
        await using (var ctx = CreateContext())
        {
            var ac = SocialTestSeams.AfterCommit(ctx);
            var joins = new JoinRequestService(ctx, SocialTestSeams.Limits(10), new RentNearBy.Infrastructure.Services.Buddy.Be4.NoticeWriter(ctx),
                new RecordingLiveHints(), SocialTestSeams.GroupChat(ctx, ac), new PublicTripReader(ctx, TimeProvider.System), ac, TimeProvider.System,
                NullLogger<JoinRequestService>.Instance);
            Assert.True((await joins.SendAsync(asker, tripId, new SendJoinRequest { ClientRequestId = Guid.NewGuid() })).Ok);
            // The 72-hour post lock (PostLockRules) is over: age the lock, then the owner makes the post private.
            await ctx.Trips.Where(t => t.Id == tripId)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.LastPublishedAt, DateTime.UtcNow - PostLockRules.Lock - TimeSpan.FromMinutes(1)));
            Assert.True((await Posts(ctx, SocialTestSeams.Limits(10)).UnpublishAsync(owner, tripId)).Ok);
        }
        await using var check = CreateContext();
        Assert.Equal(JoinRequestStatuses.Expired, await check.Set<RentNearBy.Core.Entities.Social.TripJoinRequest>()
            .Where(r => r.TripId == tripId).Select(r => r.Status).SingleAsync());
    }
}
