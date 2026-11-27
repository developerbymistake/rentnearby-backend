using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RentNearBy.Core.DTOs.Social;
using RentNearBy.Core.Entities.Social;
using RentNearBy.Core.Interfaces.Social;
using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Repositories;
using RentNearBy.Infrastructure.Services.Buddy.Be4;
using RentNearBy.Infrastructure.Services.Social;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Support.Buddy;
using RentNearBy.Tests.Support.Social;

namespace RentNearBy.Tests.Social;

// Travel levels (spec B): completedTrips + level on GET /social/me, the traveller profile person, the public trip owner
// (post, feed, rail) and the join request requester; counters.trips = completed trips only. Needs RENTNEARBY_TEST_DB (PostGIS);
// skipped without it.
public class TravelLevelIntegrationTests(TestDatabase db) : IntegrationTestBase(db)
{
    private static SocialProfileService Profiles(ApplicationDbContext ctx)
        => new(ctx, SocialTestSeams.Limits(10), new PublicTripReader(ctx, TimeProvider.System), SocialTestSeams.AfterCommit(ctx),
            TimeProvider.System, NullLogger<SocialProfileService>.Instance);

    private static PublicTripService Posts(ApplicationDbContext ctx) =>
        new(ctx, new UnitOfWork(ctx), new FakeCityCatalog(), SocialTestSeams.Limits(10, plus: true), new PublicTripReader(ctx, TimeProvider.System),
            SocialTestSeams.AfterCommit(ctx), new NoticeWriter(ctx), TimeProvider.System, NullLogger<PublicTripService>.Instance);

    private static JoinRequestService Joins(ApplicationDbContext ctx, ISeekerLimits? limits = null)
    {
        var ac = SocialTestSeams.AfterCommit(ctx);
        return new JoinRequestService(ctx, limits ?? SocialTestSeams.Limits(10, plus: true), new NoticeWriter(ctx), new RecordingLiveHints(),
            SocialTestSeams.GroupChat(ctx, ac), new PublicTripReader(ctx, TimeProvider.System), ac, TimeProvider.System,
            NullLogger<JoinRequestService>.Instance);
    }

    private static CreatePublicTripRequest Post() => new()
    {
        ClientTripId = Guid.NewGuid(), CityKeys = [FakeCityCatalog.Nainital], WhenKind = SocialRules.WhenKindWithin,
        WithinMonths = 2, Days = 3, JoinGender = JoinGenders.Anyone, Seats = 2, Text = "Easy hill walks and good food.",
    };

    // completedTrips is written only by TripCompletionCounter in production; the test sets the column directly.
    private async Task<Guid> SeekerAsync(int completedTrips)
    {
        await using var ctx = CreateContext();
        var id = (await BuddySeed.UsersAsync(ctx, 1))[0];
        await SocialSeed.ProfileAsync(ctx, id);
        await ctx.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"Users\" SET \"CompletedTripsCount\" = {completedTrips} WHERE \"Id\" = {id}");
        return id;
    }

    private async Task<Guid> PostAsync(Guid owner)
    {
        await using var ctx = CreateContext();
        var created = await Posts(ctx).CreateAsync(owner, Post());
        Assert.True(created.Ok, created.ErrorType);
        return created.Value!.TripId;
    }

    [IntegrationFact]
    public async Task My_profile_carries_completed_trips_and_level_and_the_trips_counter_ignores_live_posts()
    {
        var me = await SeekerAsync(12);
        await PostAsync(me);
        await using var ctx = CreateContext();
        var profile = (await Profiles(ctx).GetMeAsync(me)).Profile!;
        Assert.Equal(12, profile.CompletedTrips);
        Assert.Equal(2, profile.Level);
        Assert.Equal(12, profile.Counters.Trips);
        Assert.Single(profile.PublicTrips);
        Assert.Equal(12, (await Profiles(ctx).MyProfileAsync(me))!.Counters.Trips);
    }

    [IntegrationFact]
    public async Task Traveller_profile_person_carries_completed_trips_and_level()
    {
        var me = await SeekerAsync(0);
        var him = await SeekerAsync(80);
        await PostAsync(him);
        await using var ctx = CreateContext();
        var dto = (await Profiles(ctx).TravellerAsync(me, him))!;
        Assert.Equal(80, dto.Person.CompletedTrips);
        Assert.Equal(4, dto.Person.Level);
        Assert.Equal(80, dto.Counters.Trips);
    }

    [IntegrationFact]
    public async Task Private_locked_traveller_still_shows_only_count_and_level()
    {
        var me = await SeekerAsync(0);
        await using (var ctx = CreateContext())
        {
            var id = (await BuddySeed.UsersAsync(ctx, 1))[0];
            await SocialSeed.ProfileAsync(ctx, id, isPrivate: true);
            await ctx.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"Users\" SET \"CompletedTripsCount\" = {500} WHERE \"Id\" = {id}");
            var dto = (await Profiles(ctx).TravellerAsync(me, id))!;
            Assert.True(dto.PrivateLocked);
            Assert.Equal((int?)500, dto.Person.CompletedTrips);
            Assert.Equal((int?)6, dto.Person.Level);
            Assert.Empty(dto.Places);
        }
    }

    [IntegrationFact]
    public async Task Trip_post_feed_and_rail_owner_carry_completed_trips_and_level()
    {
        var owner = await SeekerAsync(30);
        var viewer = await SeekerAsync(1);
        var tripId = await PostAsync(owner);
        await using var ctx = CreateContext();
        var reader = new PublicTripReader(ctx, TimeProvider.System);

        var post = (await reader.ByIdAsync(viewer, tripId, default))!;
        Assert.Equal((int?)30, post.Owner.CompletedTrips);
        Assert.Equal((int?)3, post.Owner.Level);

        var feed = await reader.FeedAsync(viewer, null, 50, null, null, null, null, default);
        var item = feed.Items.Single(i => i.TripId == tripId);
        Assert.Equal((int?)30, item.Owner.CompletedTrips);
        Assert.Equal((int?)3, item.Owner.Level);

        var rail = await reader.RailAsync(viewer, default);
        var card = rail.Items.FirstOrDefault(i => i.TripId == tripId);
        if (card != null) Assert.Equal((int?)3, card.Owner.Level);

        var mine = (await reader.FeedAsync(owner, null, 50, null, null, null, null, default)).Mine.Single(i => i.TripId == tripId);
        Assert.Equal((int?)3, mine.Owner.Level);
    }

    [IntegrationFact]
    public async Task Join_request_requester_carries_completed_trips_and_level()
    {
        var owner = await SeekerAsync(0);
        var asker = await SeekerAsync(200);
        var tripId = await PostAsync(owner);
        await using var ctx = CreateContext();
        Assert.True((await Joins(ctx).SendAsync(asker, tripId, new SendJoinRequest { ClientRequestId = Guid.NewGuid() })).Ok);
        var page = (await Joins(ctx).ListAsync(owner, tripId))!;
        var row = page.Items.Single(i => i.Person.UserId == asker);
        Assert.Equal((int?)200, row.Person.CompletedTrips);
        Assert.Equal((int?)5, row.Person.Level);
        Assert.Equal((int?)0, page.Trip.Owner.Level);
    }

    [IntegrationFact]
    public async Task Follower_lists_do_not_carry_the_level_fields()
    {
        var me = await SeekerAsync(10);
        var other = await SeekerAsync(10);
        await using var ctx = CreateContext();
        var rows = await SocialSql.PersonRowsAsync(ctx, me, [other], default);
        Assert.Null(rows.Single().CompletedTrips);
        Assert.Null(rows.Single().Level);
        Assert.True(await ctx.Set<UserProfile>().AnyAsync(p => p.UserId == other));
    }
}
