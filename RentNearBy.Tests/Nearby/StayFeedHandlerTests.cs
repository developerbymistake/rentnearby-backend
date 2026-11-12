using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using RentNearBy.Api.Handlers;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Infrastructure.Services;

namespace RentNearBy.Tests.Nearby;

public class StayFeedHandlerTests
{
    private sealed record FakeStay(Guid Id, string City, double Lat, double Lng, short Rank = 0, bool Featured = false);

    // Same ordering and keyset rules as the SQL, over an in-memory list.
    private sealed class FakeQuery(List<FakeStay> stays, bool ignoreFeaturedRadius = false) : INearbyFeedQuery
    {
        private static double M(FakeStay s, double lat, double lng) => NearbyContext.HaversineKm(lat, lng, s.Lat, s.Lng) * 1000;

        public Task<IReadOnlyList<NearbyRow>> PageAsync(NearbyKind kind, double lat, double lng, NearbyToken? after, int take,
            NearbyFilter filter, CancellationToken ct = default)
        {
            IEnumerable<NearbyRow> q = stays.Select(s => new NearbyRow(s.Id, M(s, lat, lng)));
            if (after != null)
                q = q.Where(r => r.DistanceMeters > after.DistanceMeters
                    || (r.DistanceMeters == after.DistanceMeters && r.Id.CompareTo(after.Id) > 0));
            return Task.FromResult<IReadOnlyList<NearbyRow>>(q.OrderBy(r => r.DistanceMeters).ThenBy(r => r.Id).Take(take).ToList());
        }

        public Task<IReadOnlyList<NearbyRow>> FeaturedAsync(NearbyKind kind, double lat, double lng, double maxKm, int take,
            NearbyFilter filter, CancellationToken ct = default)
        {
            var q = stays.Where(s => s.Rank > 0 || s.Featured)
                .Select(s => (s, m: M(s, lat, lng)))
                .Where(x => ignoreFeaturedRadius || x.m <= maxKm * 1000)
                .OrderByDescending(x => x.s.Rank).ThenByDescending(x => x.s.Featured).ThenBy(x => x.m).ThenBy(x => x.s.Id)
                .Take(take).Select(x => new NearbyRow(x.s.Id, x.m)).ToList();
            return Task.FromResult<IReadOnlyList<NearbyRow>>(q);
        }
    }

    private sealed class FakeContext(bool catalogUp = true) : INearbyContext
    {
        private static readonly (string Name, string State, double Lat, double Lng)[] Cities =
        [
            ("Haldwani", "Uttarakhand", 29.2183, 79.5130), ("Nainital", "Uttarakhand", 29.3919, 79.4542),
            ("Shimla", "Himachal Pradesh", 31.1048, 77.1734),
        ];

        public Task<NearbyCity?> NearestCityAsync(double lat, double lng, CancellationToken ct = default)
        {
            if (!catalogUp) return Task.FromResult<NearbyCity?>(null);
            var c = Cities.MinBy(x => NearbyContext.HaversineKm(lat, lng, x.Lat, x.Lng));
            return Task.FromResult<NearbyCity?>(new NearbyCity(1, c.Name, c.State, c.Name, c.Lat, c.Lng,
                NearbyContext.HaversineKm(lat, lng, c.Lat, c.Lng)));
        }

        public async Task<NearbyHeaderDto> HeaderAsync(double lat, double lng, double? nearestKm, CancellationToken ct = default)
        {
            var c = await NearestCityAsync(lat, lng, ct);
            return new NearbyHeaderDto
            {
                CentreCityName = c?.Name, CentreStateName = c?.StateName, CentreCityKm = c?.DistanceKm,
                LivesIn = "We are live in Uttarakhand and Himachal Pradesh",
                FarAway = nearestKm is > NearbyRules.NearKm, NearestKm = nearestKm, CitiesUnavailable = !catalogUp,
            };
        }
    }

    private static Task<Dictionary<Guid, StayFeedItemDto>> Load(List<FakeStay> stays, IReadOnlyList<NearbyRow> rows)
    {
        var map = new Dictionary<Guid, StayFeedItemDto>();
        foreach (var r in rows)
        {
            var s = stays.Single(x => x.Id == r.Id);
            var km = NearbyRules.StayKm(r.DistanceMeters);
            map[r.Id] = new StayFeedItemDto
            {
                Id = r.Id, Name = s.City + " stay", CityName = s.City, PriorityRank = s.Rank, IsFeatured = s.Featured,
                DistanceKm = km, IsNear = NearbyRules.IsNear(km),
            };
        }
        return Task.FromResult(map);
    }

    private static Task<NearbyPageDto<StayFeedItemDto>> Run(List<FakeStay> stays, double lat, double lng, int size = 20,
        NearbyToken? after = null, bool catalogUp = true, bool ignoreFeaturedRadius = false)
        => StayFeedHandlers.BuildAsync(new NearbyArgs(lat, lng, size, after), new NearbyFilter(),
            new FakeQuery(stays, ignoreFeaturedRadius), new FakeContext(catalogUp), rows => Load(stays, rows), default);

    private static FakeStay At(string city, double lat, double lng, short rank = 0, bool featured = false)
        => new(Guid.NewGuid(), city, lat, lng, rank, featured);

    private static readonly (double Lat, double Lng) Haldwani = (29.2183, 79.5130);

    [Fact]
    public async Task Haldwani_centre_lists_haldwani_stays_first_labelled_in_city()
    {
        var stays = new List<FakeStay> { At("Nainital", 29.3919, 79.4542), At("Haldwani", 29.2200, 79.5140), At("Haldwani", 29.2300, 79.5200) };
        var page = await Run(stays, 29.2190, 79.5135);
        Assert.Equal(["Haldwani", "Haldwani", "Nainital"], page.Items.Select(i => i.CityName));
        Assert.Equal("inCity", page.Items[0].LabelKey);
        Assert.Equal("far", page.Items[2].LabelKey);
        Assert.False(page.Header.FarAway);
        Assert.Equal("Haldwani", page.Header.CentreCityName);
        Assert.True(page.Items[0].DistanceKm <= NearbyRules.NearKm);
    }

    [Fact]
    public async Task A_village_pin_gets_far_label_and_nearest_town()
    {
        var stays = new List<FakeStay> { At("Haldwani", 29.2183, 79.5130), At("Nainital", 29.3919, 79.4542) };
        var page = await Run(stays, 29.0300, 79.2000);
        Assert.Equal("Haldwani", page.Items[0].CityName);
        Assert.Equal("far", page.Items[0].LabelKey);
        Assert.False(page.Items[0].IsNear);
        Assert.True(page.Header.FarAway);
        Assert.Equal(page.Items[0].DistanceKm, page.Header.NearestKm);
        Assert.Equal("Haldwani", page.Header.CentreCityName);
    }

    [Theory]
    [InlineData(28.6139, 77.2090)]
    [InlineData(18.5204, 73.8567)]
    public async Task Delhi_and_Pune_pins_still_get_the_nearest_stays_flagged_far(double lat, double lng)
    {
        var stays = new List<FakeStay> { At("Haldwani", 29.2183, 79.5130), At("Nainital", 29.3919, 79.4542), At("Shimla", 31.1048, 77.1734) };
        var page = await Run(stays, lat, lng);
        Assert.Equal(3, page.Items.Count);
        Assert.All(page.Items, i => Assert.Equal("far", i.LabelKey));
        Assert.All(page.Items, i => Assert.True(i.DistanceKm > 100));
        Assert.True(page.Header.FarAway);
        Assert.Equal("We are live in Uttarakhand and Himachal Pradesh", page.Header.LivesIn);
        Assert.Empty(page.Featured);
        Assert.False(page.HasMore);
    }

    [Fact]
    public async Task Empty_only_when_no_stay_exists_at_all()
    {
        var page = await Run([], 28.6139, 77.2090);
        Assert.Empty(page.Items);
        Assert.Empty(page.Featured);
        Assert.False(page.HasMore);
        Assert.Null(page.NextToken);
        Assert.Null(page.Header.NearestKm);
        Assert.False(page.Header.FarAway);
        Assert.NotEqual("", page.Header.LivesIn);
    }

    [Fact]
    public async Task Pagination_walks_every_stay_once_in_distance_order_including_ties()
    {
        var stays = new List<FakeStay>();
        for (var i = 0; i < 6; i++) stays.Add(At("Haldwani", Haldwani.Lat, Haldwani.Lng));
        for (var i = 0; i < 19; i++) stays.Add(At("Nainital", 29.3919 + i * 0.01, 79.4542));
        var seen = new List<Guid>();
        NearbyToken? after = null;
        var pages = 0;
        while (true)
        {
            var page = await Run(stays, Haldwani.Lat, Haldwani.Lng, size: 7, after: after);
            seen.AddRange(page.Items.Select(i => i.Id));
            pages++;
            if (!page.HasMore) { Assert.Null(page.NextToken); break; }
            Assert.True(NearbyToken.TryDecode(page.NextToken, out after));
            Assert.InRange(pages, 1, 10);
        }
        Assert.Equal(4, pages);
        Assert.Equal(25, seen.Count);
        Assert.Equal(25, seen.Distinct().Count());
        var expected = stays.Select(s => (s.Id, M: NearbyContext.HaversineKm(Haldwani.Lat, Haldwani.Lng, s.Lat, s.Lng) * 1000))
            .OrderBy(x => x.M).ThenBy(x => x.Id).Select(x => x.Id).ToList();
        Assert.Equal(expected, seen);
    }

    [Fact]
    public async Task Same_token_gives_the_same_page_every_time()
    {
        var stays = Enumerable.Range(0, 12).Select(i => At("Nainital", 29.39 + i * 0.01, 79.45)).ToList();
        var first = await Run(stays, Haldwani.Lat, Haldwani.Lng, size: 5);
        NearbyToken.TryDecode(first.NextToken, out var token);
        var a = await Run(stays, Haldwani.Lat, Haldwani.Lng, size: 5, after: token);
        var b = await Run(stays, Haldwani.Lat, Haldwani.Lng, size: 5, after: token);
        Assert.Equal(a.Items.Select(i => i.Id), b.Items.Select(i => i.Id));
        Assert.DoesNotContain(first.Items.Select(i => i.Id), id => a.Items.Any(x => x.Id == id));
    }

    [Fact]
    public async Task Featured_slot_holds_at_most_two_near_paid_stays_on_the_first_page_only()
    {
        var stays = new List<FakeStay>
        {
            At("Haldwani", 29.2200, 79.5140),
            At("Haldwani", 29.2250, 79.5150, rank: 1),
            At("Haldwani", 29.2300, 79.5160, rank: 3),
            At("Haldwani", 29.2350, 79.5170, featured: true),
        };
        var first = await Run(stays, Haldwani.Lat, Haldwani.Lng, size: 2);
        Assert.Equal(2, first.Featured.Count);
        Assert.Equal((short)3, first.Featured[0].PriorityRank); // highest plan first
        Assert.All(first.Featured, f => Assert.True(f.PriorityRank > 0 || f.IsFeatured));
        Assert.True(first.HasMore);

        NearbyToken.TryDecode(first.NextToken, out var token);
        var second = await Run(stays, Haldwani.Lat, Haldwani.Lng, size: 2, after: token);
        Assert.Empty(second.Featured);
    }

    [Fact]
    public async Task Paid_stay_beyond_25_km_is_not_featured_and_gets_no_ordering_bonus()
    {
        var paidFar = At("Nainital", 29.3919, 79.4542, rank: 5); // about 20 km away: inside 25
        var paidVeryFar = At("Shimla", 31.1048, 77.1734, rank: 5);
        var free = At("Haldwani", 29.2200, 79.5140);
        var stays = new List<FakeStay> { paidVeryFar, free, paidFar };
        var page = await Run(stays, Haldwani.Lat, Haldwani.Lng, ignoreFeaturedRadius: true);
        Assert.DoesNotContain(page.Featured, f => f.Id == paidVeryFar.Id);
        Assert.Equal(paidFar.Id, Assert.Single(page.Featured).Id);
        Assert.Equal([free.Id, paidFar.Id, paidVeryFar.Id], page.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task Delhi_pin_with_only_far_paid_stays_has_no_featured_slot()
    {
        var stays = new List<FakeStay> { At("Haldwani", 29.2183, 79.5130, rank: 2) };
        var page = await Run(stays, 28.6139, 77.2090);
        Assert.Empty(page.Featured);
        Assert.Single(page.Items);
    }

    [Fact]
    public async Task Catalog_down_still_returns_the_list()
    {
        var stays = new List<FakeStay> { At("Haldwani", 29.2183, 79.5130) };
        var page = await Run(stays, 29.2, 79.5, catalogUp: false);
        Assert.Single(page.Items);
        Assert.True(page.Header.CitiesUnavailable);
        Assert.Equal("near", page.Items[0].LabelKey);
    }

    [Theory]
    [InlineData(91.0, 10.0)]
    [InlineData(10.0, 181.0)]
    [InlineData(null, 10.0)]
    public async Task Bad_location_is_a_400_before_any_query(double? lat, double? lng)
    {
        var result = await StayFeedHandlers.GetFeed(lat, lng, null, null, null, null,
            new FakeQuery([]), new FakeContext(), null!, default);
        Assert.Equal(400, ((IStatusCodeHttpResult)result).StatusCode);
    }

    [Fact]
    public async Task Bad_token_is_a_400()
    {
        var result = await StayFeedHandlers.GetFeed(29.2, 79.5, null, "garbage", null, null,
            new FakeQuery([]), new FakeContext(), null!, default);
        Assert.Equal(400, ((IStatusCodeHttpResult)result).StatusCode);
    }

    [Fact]
    public async Task Empty_system_through_the_real_handler_is_a_200_without_touching_the_database()
    {
        var result = await StayFeedHandlers.GetFeed(29.2, 79.5, 10, null, null, null,
            new FakeQuery([]), new FakeContext(), null!, default);
        Assert.Equal(200, ((IStatusCodeHttpResult)result).StatusCode);
    }

    [Theory]
    [InlineData(null, 20)]
    [InlineData(0, 1)]
    [InlineData(-4, 1)]
    [InlineData(500, 40)]
    [InlineData(15, 15)]
    public void Page_size_is_clamped(int? asked, int expected)
    {
        Assert.Null(NearbyFeedShared.TryParse(29.2, 79.5, asked, null, out var args));
        Assert.Equal(expected, args.PageSize);
        Assert.True(args.FirstPage);
    }

    [Fact]
    public void Compose_only_gives_a_token_when_more_rows_exist()
    {
        var last = new NearbyRow(Guid.NewGuid(), 1234.5);
        var more = NearbyFeedShared.Compose(new NearbyHeaderDto(), Array.Empty<int>(), [1, 2], last, true);
        var done = NearbyFeedShared.Compose(new NearbyHeaderDto(), Array.Empty<int>(), [1, 2], last, false);
        Assert.True(NearbyToken.TryDecode(more.NextToken, out var t));
        Assert.Equal(last.Id, t!.Id);
        Assert.Null(done.NextToken);
    }

    [Fact]
    public void Split_trims_the_extra_row()
    {
        var rows = Enumerable.Range(0, 4).Select(i => new NearbyRow(Guid.NewGuid(), i)).ToList();
        var (page, more) = NearbyFeedShared.Split(rows, 3);
        Assert.Equal(3, page.Count);
        Assert.True(more);
        var (all, none) = NearbyFeedShared.Split(rows.Take(3).ToList(), 3);
        Assert.Equal(3, all.Count);
        Assert.False(none);
    }

    [Fact]
    public void Stay_feed_item_wire_has_the_browse_fields_plus_the_feed_fields_and_no_contact_data()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new StayFeedItemDto { Name = "x" },
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        foreach (var key in new[] { "id", "name", "thumbnailUrl", "cityName", "districtName", "minPricePerNight", "priorityRank", "isFeatured",
                     "stateName", "distanceKm", "labelKey", "isNear" })
            Assert.Contains($"\"{key}\"", json);
        foreach (var banned in new[] { "phone", "latitude", "longitude", "upi", "lat\"", "lng\"" })
            Assert.DoesNotContain(banned, json, StringComparison.OrdinalIgnoreCase);
    }
}
