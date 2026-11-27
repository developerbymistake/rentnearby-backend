using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RentNearBy.Core.Entities.Buddy;
using RentNearBy.Core.Entities.Social;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.Retention;
using RentNearBy.Infrastructure.Services.Retention.GroupB;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;

namespace RentNearBy.Tests.TripCompaction;

// Written for PostgreSQL (RENTNEARBY_TEST_DB); skipped without it. Each test archives only its own trips (ArchiveAsync by id),
// so other tests' rows in the shared database are never compacted.
public class TripCompactionIntegrationTests(TestDatabase db) : IntegrationTestBase(db)
{
    private static readonly DateTime Now = DateTime.UtcNow;
    private static readonly DateTime Old = Now.AddDays(-400);
    private static readonly DateOnly Today = IstTime.TodayIst(Now);
    private static readonly DateTime CountedAt = new(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);

    private static RetentionContext Ctx(ApplicationDbContext ctx, bool dryRun = false)
        => new RetentionContext(ctx, Now, Now.AddDays(-90), Today, Today.AddDays(-90), 90) { DryRun = dryRun, Log = NullLogger.Instance };

    private async Task<Guid> UserAsync(int completedTrips = 0)
    {
        await using var ctx = CreateContext();
        var id = Guid.NewGuid();
        ctx.Users.Add(new User
        {
            Id = id, PhoneNumber = "9" + Random.Shared.NextInt64(100_000_000, 999_999_999), Name = "Compaction", IsActive = true, IsPhoneVerified = true,
            RoleId = UserRoles.SeekerId, CompletedTripsCount = completedTrips, CreatedAt = Old, UpdatedAt = Old,
        });
        await ctx.SaveChangesAsync();
        return id;
    }

    private static string PlanJson(DateOnly start) => JsonSerializer.Serialize(new TripPlan
    {
        Settings = new TripSettings { StartDate = start },
        Legs = [new TripCityLeg { Index = 1, CityKey = 210000001, CityName = "Nainital", Latitude = 29.39, Longitude = 79.45 }],
        Days =
        [
            new TripDay { Day = 1, Leg = 1, Spots = [new TripSpot { Id = "a", Name = "Naini Lake", VisitMinutes = 60 }] },
            new TripDay { Day = 2, Leg = 1, Spots = [new TripSpot { Id = "b", Name = "Snow View Point", VisitMinutes = 60 }] },
        ],
    }, TripJson.Options);

    private async Task<Guid> TripAsync(Guid owner, string status, DateOnly? end, bool isPublic = false)
    {
        await using var ctx = CreateContext();
        var id = Guid.NewGuid();
        var start = end?.AddDays(-1);
        ctx.Trips.Add(new Trip
        {
            Id = id, OwnerUserId = owner, CityKey = 210000001, CityName = "Nainital", Title = "Lake trip", Status = status,
            PlanJson = start is { } s ? PlanJson(s) : "{}", DayCount = 2, SpotCount = 2, StartDate = start, EndDate = end,
            CompletedAt = status == TripStatuses.Completed ? CountedAt : null,
            CompletionCountedAt = status == TripStatuses.Completed ? CountedAt : null,
            IsPublic = isPublic, PublishedAt = isPublic ? Old : null, CreatedAt = Old, UpdatedAt = Old,
        });
        await ctx.SaveChangesAsync();
        return id;
    }

    private async Task<Guid> ChatRoomAsync(Guid trip, Guid member, DateTime? lastMessageAt = null)
    {
        await using var ctx = CreateContext();
        var room = new ChatRoom
        {
            Id = Guid.NewGuid(), Kind = ChatKinds.Trip, TripId = trip, Status = ChatRoomStatuses.Active, CreatedAt = Old, LastMessageAt = lastMessageAt,
        };
        ctx.Set<ChatRoom>().Add(room);
        ctx.Set<ChatRoomMember>().Add(new ChatRoomMember { RoomId = room.Id, UserId = member, ListBucket = ChatBuckets.Trips, SortAt = Old });
        await ctx.SaveChangesAsync();
        return room.Id;
    }

    private async Task<Guid> ShortlistAsync(Guid trip, Guid user)
    {
        await using var ctx = CreateContext();
        var item = new TripShortlistItem
        {
            Id = Guid.NewGuid(), TripId = trip, TravellerUserId = user, StepKey = "stay:1", Kind = TripShortlistKinds.Stay, ListingId = Guid.NewGuid(),
            ListingName = "Lake Inn", AddedAt = Old, ClosesAt = Old.AddDays(2), State = TripShortlistStates.Active,
        };
        ctx.TripShortlistItems.Add(item);
        await ctx.SaveChangesAsync();
        return item.Id;
    }

    private async Task<Guid> MemberAsync(Guid trip, Guid user, Guid owner)
    {
        await using var ctx = CreateContext();
        var m = new TripMember
        {
            Id = Guid.NewGuid(), TripId = trip, UserId = user, Status = TripMemberStatuses.Joined, InvitedByUserId = owner, InvitedAt = Old,
            InviteExpiresAt = Old.AddDays(7), JoinedAt = Old, RespondedAt = Old, CreatedAt = Old, UpdatedAt = Old,
        };
        ctx.Set<TripMember>().Add(m);
        await ctx.SaveChangesAsync();
        return m.Id;
    }

    private async Task<Guid> EnquiryAsync(Guid trip, Guid user, string status, string name = "Lake Inn")
    {
        await using var ctx = CreateContext();
        var e = new TripEnquiry
        {
            Id = Guid.NewGuid(), TripId = trip, TravellerUserId = user, OwnerUserId = user, Kind = TripEnquiryKinds.Stay, ListingId = Guid.NewGuid(),
            ListingName = name, Status = status, StepKey = "stay:" + Random.Shared.Next(1, 999), ContextJson = "{\"city\":\"Nainital\"}",
            ExpiresAt = Old, CreatedAt = Old, UpdatedAt = Old,
        };
        ctx.TripEnquiries.Add(e);
        await ctx.SaveChangesAsync();
        return e.Id;
    }

    private async Task<Trip> ReadTripAsync(Guid id)
    {
        await using var ctx = CreateContext();
        return await ctx.Trips.AsNoTracking().SingleAsync(t => t.Id == id);
    }

    private async Task<TripCompactionOutcome> ArchiveAsync(Guid id)
    {
        await using var ctx = CreateContext();
        return await new TripCompactionStore().ArchiveAsync(Ctx(ctx), id, CancellationToken.None);
    }

    [IntegrationFact]
    public async Task Old_finished_trip_keeps_a_summary_and_loses_plan_chat_and_shortlist_only()
    {
        var owner = await UserAsync(completedTrips: 5);
        var buddy = await UserAsync();
        var trip = await TripAsync(owner, TripStatuses.Completed, Today.AddDays(-200), isPublic: true);
        var room = await ChatRoomAsync(trip, owner);
        var shortlist = await ShortlistAsync(trip, owner);
        var member = await MemberAsync(trip, buddy, owner);
        var stay = await EnquiryAsync(trip, owner, TripEnquiryStatuses.ConfirmedByBoth);

        var outcome = await ArchiveAsync(trip);

        Assert.True(outcome.Archived);
        Assert.True(outcome.BytesSaved > 0);
        var row = await ReadTripAsync(trip);
        Assert.NotNull(row.ArchivedAt);
        Assert.Equal("{}", row.PlanJson.Replace(" ", ""));
        Assert.False(row.IsPublic);
        Assert.False(row.PausedByPlan);
        Assert.Equal(TripStatuses.Completed, row.Status);
        Assert.Equal(CountedAt, row.CompletionCountedAt);
        Assert.Equal(CountedAt, row.CompletedAt);
        var summary = TripArchive.Read(row.SummaryJson)!;
        var city = Assert.Single(summary.Cities);
        Assert.Equal("Nainital", city.Name);
        Assert.Equal(2, city.Nights);
        Assert.Equal(["Naini Lake", "Snow View Point"], city.Spots);
        Assert.Equal(["Lake Inn"], city.Stays);
        Assert.Equal(2, summary.MemberCount);

        await using var ctx = CreateContext();
        Assert.Equal(5, await ctx.Users.Where(u => u.Id == owner).Select(u => u.CompletedTripsCount).SingleAsync());
        Assert.False(await ctx.Set<ChatRoom>().AnyAsync(r => r.Id == room));
        Assert.False(await ctx.Set<ChatRoomMember>().AnyAsync(m => m.RoomId == room));
        Assert.False(await ctx.TripShortlistItems.AnyAsync(i => i.Id == shortlist));
        Assert.True(await ctx.Set<TripMember>().AnyAsync(m => m.Id == member));
        Assert.True(await ctx.TripEnquiries.AnyAsync(e => e.Id == stay));
    }

    [IntegrationFact]
    public async Task Archiving_twice_changes_nothing_the_second_time()
    {
        var owner = await UserAsync();
        var trip = await TripAsync(owner, TripStatuses.Completed, Today.AddDays(-150));
        Assert.True((await ArchiveAsync(trip)).Archived);
        var first = await ReadTripAsync(trip);

        Assert.False((await ArchiveAsync(trip)).Archived);
        var second = await ReadTripAsync(trip);
        Assert.Equal(first.ArchivedAt, second.ArchivedAt);
        Assert.Equal(first.SummaryJson, second.SummaryJson);
        Assert.Equal(first.UpdatedAt, second.UpdatedAt);
    }

    [IntegrationFact]
    public async Task Recent_draft_or_negotiating_trips_are_never_touched()
    {
        var owner = await UserAsync();
        var recent = await TripAsync(owner, TripStatuses.Completed, Today.AddDays(-10));
        var draftNoEnd = await TripAsync(owner, TripStatuses.Draft, null);
        var draftOld = await TripAsync(owner, TripStatuses.Draft, Today.AddDays(-300));
        var negotiating = await TripAsync(owner, TripStatuses.Planned, Today.AddDays(-300));
        await EnquiryAsync(negotiating, owner, TripEnquiryStatuses.Asked, "Asked Inn");

        foreach (var id in new[] { recent, draftNoEnd, draftOld, negotiating })
        {
            var before = await ReadTripAsync(id);
            Assert.False((await ArchiveAsync(id)).Archived);
            var after = await ReadTripAsync(id);
            Assert.Null(after.ArchivedAt);
            Assert.Equal(before.PlanJson, after.PlanJson);
        }
    }

    [IntegrationFact]
    public async Task Planned_trip_past_its_end_is_archived_too()
    {
        var owner = await UserAsync();
        var trip = await TripAsync(owner, TripStatuses.Planned, Today.AddDays(-120));
        Assert.True((await ArchiveAsync(trip)).Archived);
        var row = await ReadTripAsync(trip);
        Assert.Equal(TripStatuses.Planned, row.Status);
        Assert.Null(row.CompletionCountedAt);
    }

    [IntegrationFact]
    public async Task Dry_run_of_the_rule_writes_nothing()
    {
        var owner = await UserAsync();
        var trip = await TripAsync(owner, TripStatuses.Completed, Today.AddDays(-300));
        var room = await ChatRoomAsync(trip, owner);
        var before = await ReadTripAsync(trip);

        await using (var ctx = CreateContext())
        {
            var rule = new TripCompactionRule();
            var rc = Ctx(ctx, dryRun: true);
            while (await rule.DeleteBatchAsync(rc, 1000, CancellationToken.None) > 0) { }
            Assert.True(await rule.CountCandidatesAsync(rc, 1_000_000, CancellationToken.None) >= 1);
        }

        var after = await ReadTripAsync(trip);
        Assert.Null(after.ArchivedAt);
        Assert.Equal(before.PlanJson, after.PlanJson);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        await using var check = CreateContext();
        Assert.True(await check.Set<ChatRoom>().AnyAsync(r => r.Id == room));
    }

    [IntegrationFact]
    public async Task Trip_whose_group_chat_is_still_in_use_waits_and_keeps_its_room()
    {
        var owner = await UserAsync();
        var trip = await TripAsync(owner, TripStatuses.Completed, Today.AddDays(-200));
        var room = await ChatRoomAsync(trip, owner, lastMessageAt: Now.AddDays(-5));
        var before = await ReadTripAsync(trip);

        Assert.False((await ArchiveAsync(trip)).Archived);

        var after = await ReadTripAsync(trip);
        Assert.Null(after.ArchivedAt);
        Assert.Equal(before.PlanJson, after.PlanJson);
        await using var ctx = CreateContext();
        Assert.True(await ctx.Set<ChatRoom>().AnyAsync(r => r.Id == room));
        Assert.True(await ctx.Set<ChatRoomMember>().AnyAsync(m => m.RoomId == room));
    }

    [IntegrationFact]
    public async Task Trip_whose_group_chat_went_quiet_long_ago_is_archived()
    {
        var owner = await UserAsync();
        var trip = await TripAsync(owner, TripStatuses.Completed, Today.AddDays(-200));
        var room = await ChatRoomAsync(trip, owner, lastMessageAt: Now.AddDays(-120));

        Assert.True((await ArchiveAsync(trip)).Archived);
        await using var ctx = CreateContext();
        Assert.False(await ctx.Set<ChatRoom>().AnyAsync(r => r.Id == room));
    }
}
