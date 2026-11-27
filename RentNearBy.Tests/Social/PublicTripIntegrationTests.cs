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
            SocialTestSeams.AfterCommit(ctx), TimeProvider.System, NullLogger<PublicTripService>.Instance);

    private static CreatePublicTripRequest Post(string joinGender = JoinGenders.Anyone, string? travelMode = null) => new()
    {
        ClientTripId = Guid.NewGuid(), CityKeys = [FakeCityCatalog.Nainital, FakeCityCatalog.Almora], WhenKind = SocialRules.WhenKindWithin,
        WithinMonths = 2, Days = 4, JoinGender = joinGender, Seats = 2, Text = "Looking for two easy-going trekkers. Call 9876543210",
        TravelMode = travelMode,
    };

    private static UpdatePublicPostRequest Edit(string? travelMode) => new()
    {
        WhenKind = SocialRules.WhenKindWithin, WithinMonths = 2, Days = 4, JoinGender = JoinGenders.Anyone, Seats = 2,
        Text = "Looking for two easy-going trekkers.", TravelMode = travelMode,
    };

    private static string? Field(object? data) => (string?)data?.GetType().GetProperty("field")?.GetValue(data);

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
        var forMan = Assert.Single((await reader.FeedAsync(man, null, 20, null, null, null, null, default)).Items, p => p.TripId == tripId);
        Assert.True(forMan.NotOpen);
        Assert.NotEqual(string.Empty, forMan.Text);
        var forWoman = Assert.Single((await reader.FeedAsync(otherWoman, null, 20, null, null, null, null, default)).Items, p => p.TripId == tripId);
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
        Assert.Contains((await reader.FeedAsync(viewer, null, 20, null, JoinGenders.Men, null, null, default)).Items, p => p.TripId == men);
        Assert.DoesNotContain((await reader.FeedAsync(viewer, null, 20, null, JoinGenders.Women, null, null, default)).Items, p => p.TripId == men);
        Assert.DoesNotContain((await reader.FeedAsync(viewer, null, 20, null, JoinGenders.Anyone, null, null, default)).Items, p => p.TripId == men);
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

    // T1 + T2: not set stays null (no chip); a picked mode is stored and shown on the post, the feed, the rail and the profile list.
    [IntegrationFact]
    public async Task Travel_mode_is_optional_and_shown_everywhere_the_post_is_read()
    {
        var owner = await SeekerAsync(SocialGenders.Male);
        var viewer = await SeekerAsync(SocialGenders.Male);
        Guid plain, bike;
        await using (var ctx = CreateContext())
        {
            var posts = Posts(ctx, SocialTestSeams.Limits(10, plus: true));
            var a = await posts.CreateAsync(owner, Post());
            Assert.True(a.Ok, a.ErrorType);
            Assert.Null(a.Value!.TravelMode);
            plain = a.Value.TripId;
            var b = await posts.CreateAsync(owner, Post(travelMode: PostTravelModes.Bike));
            Assert.True(b.Ok, b.ErrorType);
            Assert.Equal(PostTravelModes.Bike, b.Value!.TravelMode);
            bike = b.Value.TripId;
        }
        await using (var check = CreateContext())
        {
            Assert.Null(await check.Trips.AsNoTracking().Where(t => t.Id == plain).Select(t => t.PostTravelMode).SingleAsync());
            Assert.Equal(PostTravelModes.Bike, await check.Trips.AsNoTracking().Where(t => t.Id == bike).Select(t => t.PostTravelMode).SingleAsync());
        }
        await using var read = CreateContext();
        var reader = new PublicTripReader(read, TimeProvider.System);
        Assert.Equal(PostTravelModes.Bike, (await reader.ByIdAsync(viewer, bike, default))!.TravelMode);
        Assert.Null((await reader.ByIdAsync(viewer, plain, default))!.TravelMode);
        var feed = (await reader.FeedAsync(viewer, null, 20, null, null, null, null, default)).Items;
        Assert.Equal(PostTravelModes.Bike, Assert.Single(feed, p => p.TripId == bike).TravelMode);
        Assert.Null(Assert.Single(feed, p => p.TripId == plain).TravelMode);
        Assert.Equal(PostTravelModes.Bike, Assert.Single((await reader.RailAsync(viewer, default)).Items, p => p.TripId == bike).TravelMode);
        Assert.Equal(PostTravelModes.Bike, Assert.Single(await reader.OfOwnerAsync(viewer, owner, default), p => p.TripId == bike).TravelMode);
        var json = JsonSerializer.Serialize(await reader.ByIdAsync(viewer, bike, default), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"travelMode\":\"bike\"", json);
    }

    // T4: unknown values never reach the column (create and edit).
    [IntegrationFact]
    public async Task Unknown_travel_mode_is_refused_with_the_field()
    {
        var owner = await SeekerAsync(SocialGenders.Female);
        await using var ctx = CreateContext();
        var posts = Posts(ctx, SocialTestSeams.Limits(10, plus: true));
        foreach (var bad in new[] { "plane", "", "Bike", "bike,car" })
        {
            var refused = await posts.CreateAsync(owner, Post(travelMode: bad));
            Assert.Equal(SocialErrorTypes.InvalidPost, refused.ErrorType);
            Assert.Equal("travelMode", Field(refused.ErrorData));
        }
        var created = await posts.CreateAsync(owner, Post(travelMode: PostTravelModes.Car));
        Assert.True(created.Ok, created.ErrorType);
        var tripId = created.Value!.TripId;
        var edit = await posts.PutPostAsync(owner, tripId, Edit("rocket"));
        Assert.Equal(SocialErrorTypes.InvalidPost, edit.ErrorType);
        Assert.Equal("travelMode", Field(edit.ErrorData));
        await using var check = CreateContext();
        Assert.Equal(PostTravelModes.Car, await check.Trips.AsNoTracking().Where(t => t.Id == tripId).Select(t => t.PostTravelMode).SingleAsync());
    }

    // T5: the owner changes the mode later; PUT replaces the stored value (null = not set again).
    [IntegrationFact]
    public async Task Edit_changes_or_clears_the_travel_mode()
    {
        var owner = await SeekerAsync(SocialGenders.Male);
        await using var ctx = CreateContext();
        var posts = Posts(ctx, SocialTestSeams.Limits(10, plus: true));
        var created = await posts.CreateAsync(owner, Post(travelMode: PostTravelModes.Public));
        Assert.True(created.Ok, created.ErrorType);
        var tripId = created.Value!.TripId;
        var taxi = await posts.PutPostAsync(owner, tripId, Edit(PostTravelModes.Taxi));
        Assert.True(taxi.Ok, taxi.ErrorType);
        Assert.Equal(PostTravelModes.Taxi, taxi.Value!.TravelMode);
        var cleared = await posts.PutPostAsync(owner, tripId, Edit(null));
        Assert.True(cleared.Ok, cleared.ErrorType);
        Assert.Null(cleared.Value!.TravelMode);
    }

    // T3: the feed filter keeps only posts with exactly that mode; posts without a mode match only 'any'.
    [IntegrationFact]
    public async Task Feed_travel_filter_keeps_only_that_mode()
    {
        var poster = await SeekerAsync(SocialGenders.Male);
        var viewer = await SeekerAsync(SocialGenders.Male);
        Guid bike, plain;
        await using (var ctx = CreateContext())
        {
            var posts = Posts(ctx, SocialTestSeams.Limits(10, plus: true));
            bike = (await posts.CreateAsync(poster, Post(travelMode: PostTravelModes.Bike))).Value!.TripId;
            plain = (await posts.CreateAsync(poster, Post())).Value!.TripId;
        }
        await using var read = CreateContext();
        var reader = new PublicTripReader(read, TimeProvider.System);
        var bikes = (await reader.FeedAsync(viewer, null, 20, null, null, null, PostTravelModes.Bike, default)).Items;
        Assert.Contains(bikes, p => p.TripId == bike);
        Assert.DoesNotContain(bikes, p => p.TripId == plain);
        var cars = (await reader.FeedAsync(viewer, null, 20, null, null, null, PostTravelModes.Car, default)).Items;
        Assert.DoesNotContain(cars, p => p.TripId == bike || p.TripId == plain);
        var any = (await reader.FeedAsync(viewer, null, 20, null, null, null, null, default)).Items;
        Assert.Contains(any, p => p.TripId == bike);
        Assert.Contains(any, p => p.TripId == plain);
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
            Assert.True(await Posts(ctx, SocialTestSeams.Limits(10)).UnpublishAsync(owner, tripId));
        }
        await using var check = CreateContext();
        Assert.Equal(JoinRequestStatuses.Expired, await check.Set<RentNearBy.Core.Entities.Social.TripJoinRequest>()
            .Where(r => r.TripId == tripId).Select(r => r.Status).SingleAsync());
    }
}
