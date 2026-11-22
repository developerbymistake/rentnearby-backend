using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Repositories;
using RentNearBy.Infrastructure.Services.Owner.Be3;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Support.Owner;
using RentNearBy.Tests.Trips;

namespace RentNearBy.Tests.Owner.Journeys;

// JT-B-PERF-1 (FB-29, FB-30, FB-32, FB-33): fewer and smaller statements on the owner and city read paths. Pure parts and source
// guards run here; the query-count and paging integration tests are written, not run.
public class OwnerReadPerfJourneyTests
{
    private static string Src(params string[] parts) => File.ReadAllText(Path.Combine([CompletionCounterGuardTests.Root(), .. parts]));

    // FB-29: the page is chosen first with the exact circle; the laterals join the page only.
    [Fact]
    public void City_stay_page_is_chosen_before_the_laterals()
    {
        var src = Src("RentNearBy.Infrastructure", "Repositories", "StayListingRepository.cs");
        var start = src.IndexOf("public async Task<IEnumerable<StayNearbyDto>> GetNearbyAsync(", StringComparison.Ordinal);
        var body = src[start..src.IndexOf("public async Task<IEnumerable<StayNearbyDto>> GetNearestAsync(", StringComparison.Ordinal)];
        int At(string s) => body.IndexOf(s, StringComparison.Ordinal);
        Assert.True(At("WITH page AS") < At("ST_DWithin("));
        Assert.True(At("ST_DWithin(") < At("LIMIT {limit}::int4 OFFSET {skip}::int4"));
        Assert.True(At("LIMIT {limit}::int4 OFFSET {skip}::int4") < At("FROM page pg"));
        Assert.True(At("FROM page pg") < At("LEFT JOIN LATERAL"));
        Assert.DoesNotContain(".Where(x => x.Dist <= radiusKm)", body);
    }

    // FB-30: the owner graph reads split, and the hub folds hidden photos and rooms into its one facts statement.
    [Fact]
    public void Hub_reads_are_split_and_folded()
    {
        var repo = Src("RentNearBy.Infrastructure", "Repositories", "StayListingRepository.cs");
        Assert.True(repo.Split(".AsSplitQuery()").Length - 1 >= 4);
        Assert.Contains("GetLiveByUserReadAsync(userId)", Src("RentNearBy.Api", "Handlers", "StayHandlers.cs"));
        Assert.Contains("AS \"HiddenPhotos\"", StayHubReader.FactsSql);
        Assert.Contains("AS \"HiddenRooms\"", StayHubReader.FactsSql);
        var hub = Src("RentNearBy.Infrastructure", "Services", "Owner", "Be3", "StayHubReader.cs");
        Assert.DoesNotContain("db.StayPhotos", hub);
        Assert.DoesNotContain("db.StayRooms", hub);
    }

    [Fact]
    public void Hidden_rows_parse_from_the_facts_json()
    {
        var id = Guid.NewGuid();
        var photos = StayHubReader.ParseHiddenPhotos($$"""[{"id":"{{id}}","url":"https://x/p.jpg","photoOrder":3}]""");
        Assert.Equal(id, Assert.Single(photos).Id);
        Assert.Equal(3, photos[0].PhotoOrder);
        var rooms = StayHubReader.ParseHiddenRooms($$"""[{"id":"{{id}}","roomIndex":101,"pricePerNight":2500,"maxGuests":2,"roomName":"Deluxe","meals":"Breakfast","floor":null}]""");
        Assert.Equal("Deluxe", Assert.Single(rooms).RoomName);
        Assert.Null(rooms[0].Floor);
        Assert.Empty(StayHubReader.ParseHiddenPhotos(null));
        Assert.Empty(StayHubReader.ParseHiddenRooms(null));
    }

    // FB-32: keyset cursor round trip and refusals.
    [Fact]
    public void Owner_enquiry_cursor_round_trips()
    {
        var at = new DateTime(2026, 11, 22, 7, 30, 0, DateTimeKind.Utc);
        var id = Guid.NewGuid();
        Assert.True(OwnerEnquiryCursor.TryDecode(OwnerEnquiryCursor.Encode(at, id), out var backAt, out var backId));
        Assert.Equal(at, backAt);
        Assert.Equal(id, backId);
        Assert.False(OwnerEnquiryCursor.TryDecode("abc", out _, out _));
        Assert.False(OwnerEnquiryCursor.TryDecode("-5_" + id.ToString("N"), out _, out _));
        Assert.False(OwnerEnquiryCursor.TryDecode(null, out _, out _));
    }

    [Fact]
    public void Owner_enquiry_counts_can_be_skipped_and_paging_is_keyset()
    {
        var src = Src("RentNearBy.Api", "Handlers", "TripOwnerHandlers.cs");
        Assert.Contains("counts == false ? null : await OwnerCountsAsync", src);
        Assert.Contains("e.CreatedAt < afterAt || (e.CreatedAt == afterAt && e.Id.CompareTo(afterId) < 0)", src);
    }

    // FB-33: GET /me reads the owner's rows with one statement.
    [Theory]
    [InlineData(StayStatuses.Approved, false, false, true, false)]
    public void Owner_facts_follow_the_old_four_reads(string status, bool deleted, bool everApproved, bool submitted, bool hasDeleted)
    {
        var f = OwnerListingFacts.Of([new(status, deleted, null, everApproved)], StayStatuses.Draft);
        Assert.Equal(status, f.Status);
        Assert.Equal(submitted, f.SubmittedHistory);
        Assert.Equal(everApproved, f.EverApproved);
        Assert.Equal(hasDeleted, f.HasDeleted);
    }

    [Fact]
    public void Owner_facts_cover_drafts_and_deleted_history()
    {
        var draftOnly = OwnerListingFacts.Of([new(StayStatuses.Draft, false, null, false)], StayStatuses.Draft);
        Assert.Equal((StayStatuses.Draft, false, false, false), (draftOnly.Status, draftOnly.SubmittedHistory, draftOnly.EverApproved, draftOnly.HasDeleted));

        var deletedApproved = OwnerListingFacts.Of([new(StayStatuses.Approved, true, DateTime.UtcNow, true)], StayStatuses.Draft);
        Assert.Null(deletedApproved.Status);
        Assert.True(deletedApproved.SubmittedHistory && deletedApproved.EverApproved && deletedApproved.HasDeleted);

        var none = OwnerListingFacts.Of([], StayStatuses.Draft);
        Assert.Equal((null as string, false, false, false), (none.Status, none.SubmittedHistory, none.EverApproved, none.HasDeleted));
        Assert.Contains("GetOwnerFactsAsync(user.Id)", Src("RentNearBy.Api", "Handlers", "UsersHandlers.cs"));
        Assert.DoesNotContain("HasDeletedAsync(user.Id)", Src("RentNearBy.Api", "Handlers", "UsersHandlers.cs"));
    }
}

// Integration (WRITTEN, NOT RUN: needs RENTNEARBY_TEST_DB with PostGIS).
public class OwnerReadPerfJourneyIntegrationTests(TestDatabase db) : IntegrationTestBase(db)
{
    // FB-29: a page cut by the circle in memory used to end paging early; now every page is full until the last one.
    [IntegrationFact]
    public async Task Paging_returns_every_stay_inside_the_circle()
    {
        await using (var c = CreateContext())
        {
            for (var i = 0; i < 12; i++)
            {
                var owner = await OwnerSeed.StayOwnerAsync(c, name: $"Stay {i}");
                await OwnerSeed.PhotosAsync(c, owner.ListingId, visible: 1);
            }
        }
        await using var db = CreateContext();
        var repo = new StayListingRepository(db);
        var first = (await repo.GetNearbyAsync(29.3919, 79.4542, 5, null, null, take: 5, skip: 0)).ToList();
        var second = (await repo.GetNearbyAsync(29.3919, 79.4542, 5, null, null, take: 5, skip: 5)).ToList();
        Assert.Equal(5, first.Count);
        Assert.Equal(5, second.Count);
        Assert.Empty(first.Select(s => s.Id).Intersect(second.Select(s => s.Id)));
    }
}
