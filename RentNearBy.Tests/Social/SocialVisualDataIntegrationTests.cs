using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.Buddy.Be1;
using RentNearBy.Infrastructure.Services.Buddy.Be2;
using RentNearBy.Infrastructure.Services.Buddy.Be4;
using RentNearBy.Infrastructure.Services.Social;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Support.Buddy;
using RentNearBy.Tests.Support.Social;

namespace RentNearBy.Tests.Social;

// Mock frames C8c (My Buddies row), B6 (blocked count) and E13b (trip room faces). Needs RENTNEARBY_TEST_DB; skipped without it.
public class SocialVisualDataIntegrationTests(TestDatabase db) : IntegrationTestBase(db)
{
    private static FollowService Follows(ApplicationDbContext ctx)
        => new(ctx, new NoticeWriter(ctx), SocialTestSeams.AfterCommit(ctx), NullLogger<FollowService>.Instance);

    private static BuddyListService Lists(ApplicationDbContext ctx) => new(ctx, new RecordingLiveHints(), NullLogger<BuddyListService>.Instance);

    private static SocialProfileService Profiles(ApplicationDbContext ctx)
        => new(ctx, SocialTestSeams.Limits(10), new PublicTripReader(ctx, TimeProvider.System), SocialTestSeams.AfterCommit(ctx),
            TimeProvider.System, NullLogger<SocialProfileService>.Instance);

    private static SocialChatService Chat(ApplicationDbContext ctx)
        => new(ctx, SocialTestSeams.Limits(10), new NoticeWriter(ctx), SocialTestSeams.AfterCommit(ctx), TimeProvider.System,
            NullLogger<SocialChatService>.Instance);

    private static TripInviteService Invites(ApplicationDbContext ctx)
    {
        var ac = SocialTestSeams.AfterCommit(ctx);
        return new(ctx, new NoticeWriter(ctx), new RecordingLiveHints(), TimeProvider.System, NullLogger<TripInviteService>.Instance,
            SocialTestSeams.Limits(10), SocialTestSeams.GroupChat(ctx, ac), ac);
    }

    [IntegrationFact]
    public async Task Buddy_row_carries_gender_trips_together_and_mutual_count()
    {
        List<Guid> p;
        await using (var ctx = CreateContext())
        {
            p = await BuddySeed.UsersAsync(ctx, 4);
            await SocialSeed.ProfileAsync(ctx, p[0]);
            await SocialSeed.ProfileAsync(ctx, p[1], SocialGenders.Female);
            await SocialSeed.ProfileAsync(ctx, p[2]);
            await SocialSeed.ProfileAsync(ctx, p[3]);
        }
        await using (var ctx = CreateContext())
        {
            Assert.True((await Follows(ctx).FollowAsync(p[1], p[0])).Ok);
            Assert.True((await Follows(ctx).FollowAsync(p[0], p[2])).Ok);
            Assert.True((await Follows(ctx).FollowAsync(p[2], p[1])).Ok);
            await BuddySeed.BuddiesAsync(ctx, p[0], [p[1]]);
            var mine = await BuddySeed.TripAsync(ctx, p[0]);
            await BuddySeed.MembersAsync(ctx, mine, p[0], [p[1]]);
            var third = await BuddySeed.TripAsync(ctx, p[3]);
            await BuddySeed.MembersAsync(ctx, third, p[3], [p[0], p[1]]);
            var left = await BuddySeed.TripAsync(ctx, p[1]);
            await BuddySeed.MembersAsync(ctx, left, p[1], [p[0]], TripMemberStatuses.Left);
        }

        await using var check = CreateContext();
        var row = Assert.Single((await Lists(check).BuddiesAsync(p[0], null, 50, null))!.Items);
        Assert.Equal(p[1], row.UserId);
        Assert.Equal(SocialGenders.Female, row.Gender);
        Assert.Equal(2, row.TripsTogether);
        Assert.Equal(1, row.MutualCount);
    }

    [IntegrationFact]
    public async Task Social_me_counts_the_people_i_blocked()
    {
        List<Guid> p;
        await using (var ctx = CreateContext())
        {
            p = await BuddySeed.UsersAsync(ctx, 4);
            await SocialSeed.ProfileAsync(ctx, p[0]);
            var now = DateTime.UtcNow;
            ctx.UserBlocks.AddRange(
                new UserBlock { Id = Guid.NewGuid(), BlockerId = p[0], BlockedId = p[1], CreatedAt = now },
                new UserBlock { Id = Guid.NewGuid(), BlockerId = p[0], BlockedId = p[2], CreatedAt = now },
                new UserBlock { Id = Guid.NewGuid(), BlockerId = p[3], BlockedId = p[0], CreatedAt = now });
            await ctx.SaveChangesAsync();
        }

        await using var check = CreateContext();
        Assert.Equal(2, (await Profiles(check).GetMeAsync(p[0])).Counts.Blocked);
    }

    [IntegrationFact]
    public async Task Trip_room_faces_put_photos_first_with_matching_initials_and_the_city_key()
    {
        List<Guid> p;
        Guid trip;
        List<Guid> invites;
        await using (var ctx = CreateContext())
        {
            p = await BuddySeed.UsersAsync(ctx, 3);
            trip = await BuddySeed.TripAsync(ctx, p[0]);
            invites = await BuddySeed.MembersAsync(ctx, trip, p[0], [p[1], p[2]], TripMemberStatuses.Invited);
            var a = await ctx.Users.SingleAsync(u => u.Id == p[1]);
            a.Name = "Rahul";
            var b = await ctx.Users.SingleAsync(u => u.Id == p[2]);
            b.Name = "Meera";
            b.ProfilePhotoUrl = "https://example.com/meera.jpg";
            await ctx.SaveChangesAsync();
        }
        await using (var ctx = CreateContext())
            Assert.True((await Invites(ctx).AcceptAsync(invites[0], p[1])).Ok);
        await using (var ctx = CreateContext())
            Assert.True((await Invites(ctx).AcceptAsync(invites[1], p[2])).Ok);

        await using var check = CreateContext();
        var room = await Chat(check).TripRoomAsync(p[0], trip);
        Assert.NotNull(room?.Trip);
        Assert.Equal(new[] { "https://example.com/meera.jpg" }, room!.Trip!.PhotoUrls);
        Assert.Equal(new[] { "M", "R" }, room.Trip.MemberInitials);
        Assert.Equal(210000001L, room.Trip.CityKey);
    }
}
