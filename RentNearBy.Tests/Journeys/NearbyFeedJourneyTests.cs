using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using RentNearBy.Api.Handlers;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Infrastructure.Repositories;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Tests.Nearby;
using RentNearBy.Tests.Taxis;
using RentNearBy.Tests.Trips;

namespace RentNearBy.Tests.Journeys;

// Nearby feed journeys through the real handlers and composers. Only the SQL and the hydrators are replaced by in-memory fakes.
public class NearbyFeedJourneyTests
{
    private static readonly (double Lat, double Lng) Haldwani = (29.2183, 79.5130), Nainital = (29.3919, 79.4542),
        Village = (29.0300, 79.2000), Delhi = (28.6139, 77.2090), Mussoorie = (30.4598, 78.0644);

    private sealed record S(Guid Id, string City, double Lat, double Lng, short Rank = 0);

    private sealed class StayQuery(List<S> stays) : INearbyFeedQuery
    {
        private double M(S s, double la, double lo) => FakeFeedQuery.Km(la, lo, s.Lat, s.Lng) * 1000;

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
            var q = stays.Where(s => s.Rank > 0).Select(s => new NearbyRow(s.Id, M(s, lat, lng)))
                .Where(r => r.DistanceMeters <= maxKm * 1000).OrderBy(r => r.DistanceMeters).ThenBy(r => r.Id).Take(take).ToList();
            return Task.FromResult<IReadOnlyList<NearbyRow>>(q);
        }
    }

    private static S Stay(string city, (double Lat, double Lng) p, short rank = 0) => new(Guid.NewGuid(), city, p.Lat, p.Lng, rank);

    private static (double, double) Off((double Lat, double Lng) p, double dLat, double dLng) => (p.Lat + dLat, p.Lng + dLng);

    private static Task<NearbyPageDto<StayFeedItemDto>> Feed(List<S> stays, (double Lat, double Lng) at, int size = 20, NearbyToken? after = null)
        => StayFeedHandlers.BuildAsync(new NearbyArgs(at.Lat, at.Lng, size, after), new NearbyFilter(), new StayQuery(stays), new FakeFeedContext(),
            rows =>
            {
                var map = new Dictionary<Guid, StayFeedItemDto>();
                foreach (var r in rows)
                {
                    var s = stays.Single(x => x.Id == r.Id);
                    var km = NearbyRules.StayKm(r.DistanceMeters);
                    map[r.Id] = new StayFeedItemDto
                    {
                        Id = s.Id, Name = s.City + " stay", CityName = s.City, PriorityRank = s.Rank, DistanceKm = km,
                        LabelKey = NearbyRules.LabelKey(km, s.City, null), IsNear = NearbyRules.IsNear(km),
                    };
                }
                return Task.FromResult(map);
            }, default);

    private static List<S> Supply() =>
    [
        Stay("Haldwani", (29.2200, 79.5140)), Stay("Haldwani", (29.2300, 79.5200)), Stay("Nainital", Nainital),
        Stay("Nainital", (29.3950, 79.4600)), Stay("Mussoorie", Mussoorie),
    ];

    [Fact]
    public async Task User_in_haldwani_sees_haldwani_stays_first_and_no_far_flag()
    {
        var page = await Feed(Supply(), Haldwani);

        Assert.Equal(["Haldwani", "Haldwani", "Nainital", "Nainital", "Mussoorie"], page.Items.Select(i => i.CityName));
        Assert.Equal("inCity", page.Items[0].LabelKey);
        Assert.Equal("far", page.Items[2].LabelKey);
        Assert.False(page.Header.FarAway);
    }

    [Fact]
    public async Task User_in_a_village_gets_the_nearest_stays_with_far_labels_and_the_far_flag()
    {
        var page = await Feed(Supply(), Village);

        Assert.Equal("Haldwani", page.Items[0].CityName);
        Assert.All(page.Items, i => { Assert.Equal("far", i.LabelKey); Assert.False(i.IsNear); });
        Assert.True(page.Header.FarAway);
        Assert.False(string.IsNullOrWhiteSpace(page.Header.LivesIn));
    }

    [Fact]
    public async Task User_in_delhi_still_gets_the_nearest_listings_flagged_far()
    {
        var page = await Feed(Supply(), Delhi);

        Assert.Equal(5, page.Items.Count);
        Assert.True(page.Header.FarAway);
        Assert.All(page.Items, i => Assert.True(i.DistanceKm > 15));
        Assert.Equal(page.Items.OrderBy(i => i.DistanceKm).Select(i => i.Id), page.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task Paging_through_many_pages_has_no_duplicate_and_no_gap_even_with_equal_distances()
    {
        var stays = Enumerable.Range(0, 37).Select(i => Stay("Haldwani", Off(Haldwani, (i / 3) * 0.002, 0))).ToList();
        var seen = new List<Guid>();
        NearbyToken? after = null;
        var pages = 0;
        while (true)
        {
            var page = await Feed(stays, Haldwani, 8, after);
            seen.AddRange(page.Items.Select(i => i.Id));
            pages++;
            if (!page.HasMore) break;
            Assert.True(NearbyToken.TryDecode(page.NextToken, out after));
            Assert.True(pages < 20);
        }

        Assert.Equal(5, pages);
        Assert.Equal(37, seen.Count);
        Assert.Equal(37, seen.Distinct().Count());
        Assert.Equal(stays.Select(s => s.Id).OrderBy(x => x), seen.OrderBy(x => x));
    }

    [Fact]
    public async Task Featured_slot_is_at_most_two_near_paid_stays_on_the_first_page_and_order_is_distance_only()
    {
        var paidFar = Stay("Nainital", Nainital, 3);
        var p1 = Stay("Haldwani", (29.2400, 79.5200), 2);
        var p2 = Stay("Haldwani", (29.2500, 79.5200), 1);
        var p3 = Stay("Haldwani", (29.2600, 79.5200), 1);
        var free = Stay("Haldwani", (29.2190, 79.5135));
        var stays = new List<S> { paidFar, p1, p2, p3, free };

        var first = await Feed(stays, Haldwani, 2);

        Assert.Equal(2, first.Featured.Count);
        Assert.DoesNotContain(paidFar.Id, first.Featured.Select(f => f.Id));
        Assert.Equal(free.Id, first.Items[0].Id);
        Assert.True(NearbyToken.TryDecode(first.NextToken, out var tok));
        var second = await Feed(stays, Haldwani, 2, tok);
        Assert.Empty(second.Featured);
    }

    [Fact]
    public async Task Delhi_user_with_only_far_paid_stays_gets_no_featured_slot()
    {
        var page = await Feed([Stay("Nainital", Nainital, 3), Stay("Haldwani", Haldwani)], Delhi);

        Assert.Empty(page.Featured);
        Assert.Equal(2, page.Items.Count);
    }

    private static FDriver D(string name, string city, (double Lat, double Lng) p) => new(name, city, p.Lat, p.Lng, []);

    private static Task<NearbyPageDto<TaxiFeedItemDto>> TaxiLocal(IReadOnlyList<FDriver> ds, (double Lat, double Lng) at, int size = 20)
        => TaxiFeedComposer.LocalAsync(new NearbyArgs(at.Lat, at.Lng, size, null), null, new FakeFeedQuery(ds), new FakeFeedContext(), TaxiFeedFixture.Hydrate(ds));

    [Fact]
    public async Task Taxi_local_feed_rounds_distance_up_to_five_km_and_carries_no_coordinates()
    {
        var ds = new[] { D("A", "Haldwani", Haldwani), D("B", "Nainital", Nainital), D("C", "Mussoorie", Mussoorie) };

        var page = await TaxiLocal(ds, Village);

        Assert.Equal(3, page.Items.Count);
        Assert.All(page.Items, i => { Assert.True(i.DistanceKm >= 5); Assert.Equal(0, i.DistanceKm % 5); });
        Assert.True(page.Header.FarAway);
        Assert.DoesNotContain("latitude", System.Text.Json.JsonSerializer.Serialize(page), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Driver_pins_in_one_two_km_cell_snap_to_the_same_point_and_snap_is_stable()
    {
        var a = NearbyRules.TaxiGridSnap(29.21831, 79.51301);
        var b = NearbyRules.TaxiGridSnap(29.21850, 79.51320);

        Assert.Equal(a, b);
        Assert.Equal(a, NearbyRules.TaxiGridSnap(a.Lat, a.Lng));
        var moved = FakeFeedQuery.Km(29.21831, 79.51301, a.Lat, a.Lng);
        Assert.True(moved <= 2 * Math.Sqrt(2));
    }

    [Fact]
    public async Task Two_drivers_a_few_hundred_metres_apart_show_the_same_distance()
    {
        var ds = new[] { D("Home", "Haldwani", (29.21831, 79.51301)), D("Next door", "Haldwani", (29.21850, 79.51320)) };

        var page = await TaxiLocal(ds, Delhi);

        Assert.Equal(page.Items[0].DistanceKm, page.Items[1].DistanceKm);
    }

    private static (double, double, double, double) R((double Lat, double Lng) a, (double Lat, double Lng) b) => (a.Lat, a.Lng, b.Lat, b.Lng);

    private static Task<TaxiRouteFeedDto> Route(IReadOnlyList<FDriver> ds, (double Lat, double Lng) from, (double Lat, double Lng) to)
        => TaxiFeedComposer.RouteAsync(new TaxiRouteFeedArgs(from.Lat, from.Lng, to.Lat, to.Lng, null, 5, null, null),
            TaxiFeedFixture.OnRoute(ds, from.Lat, from.Lng, to.Lat, to.Lng), TaxiFeedFixture.NoWays(),
            new FakeFeedQuery(ds), new FakeFeedContext(), TaxiFeedFixture.Hydrate(ds));

    private static TaxiTierDto Tier(TaxiRouteFeedDto d, string key) => d.Tiers.Single(t => t.Key == key);

    [Fact]
    public async Task Owner_example_haldwani_to_nainital_lists_route_driver_then_based_nearby_without_route_claim()
    {
        var onRoute = new FDriver("Route", "Haldwani", Haldwani.Lat, Haldwani.Lng, [R(Haldwani, Nainital)]);
        var other = new FDriver("Other", "Nainital", Nainital.Lat, Nainital.Lng, [R(Nainital, Delhi)]);

        var d = await Route([onRoute, other], Haldwani, Nainital);

        Assert.Equal([onRoute.Id], Tier(d, "onRoute").Items.Select(i => i.Id));
        Assert.All(Tier(d, "basedNearFrom").Items.Concat(Tier(d, "inTo").Items), i => { Assert.False(i.ListedForRoute); Assert.Null(i.MatchedFare); });
        Assert.Contains(other.Id, d.Tiers.SelectMany(t => t.Items).Select(i => i.Id));
    }

    [Fact]
    public async Task Owner_example_village_to_delhi_is_never_empty_when_any_taxi_exists()
    {
        var ds = new[] { new FDriver("A", "Haldwani", Haldwani.Lat, Haldwani.Lng, [R(Haldwani, Delhi)]) };

        var d = await Route(ds, Village, Delhi);

        Assert.NotEmpty(d.Tiers.SelectMany(t => t.Items));
        Assert.True(Tier(d, "basedNearFrom").Header!.FarAway || Tier(d, "inTo").Header!.FarAway);
    }

    [Fact]
    public async Task Owner_example_delhi_to_mussoorie_shows_the_nearest_taxis_to_mussoorie_with_distance()
    {
        var ds = new[] { new FDriver("M", "Mussoorie", Mussoorie.Lat, Mussoorie.Lng, []), new FDriver("H", "Haldwani", Haldwani.Lat, Haldwani.Lng, []) };

        var d = await Route(ds, Delhi, Mussoorie);

        var to = Tier(d, "inTo").Items.Concat(Tier(d, "basedNearFrom").Items).ToList();
        Assert.Equal(2, to.Count);
        Assert.All(to, i => Assert.NotNull(i.DistanceKm));
    }

    [Fact]
    public async Task Route_feed_is_empty_only_when_the_whole_system_has_no_taxi()
    {
        var d = await Route([], Haldwani, Nainital);

        Assert.Empty(d.Tiers.SelectMany(t => t.Items));
        Assert.Equal(4, d.Tiers.Count);
    }

    [Fact]
    public void Taxi_without_a_rate_cannot_be_submitted_and_the_rate_form_rejects_missing_values()
    {
        var t = new TaxiListing { DrivingLicenceNumberEnc = "x", DateOfBirth = new DateOnly(1985, 1, 1), LocalDayRate = null, LocalDayKm = null };
        t.Routes.Add(new TaxiRoute { RouteIndex = 1, FromName = "A", ToName = "B", Fare = 1500, TripType = "OneWay" });

        Assert.False(TaxiRateRules.Done(t));
        t.LocalDayRate = 1800; t.LocalDayKm = 80;
        Assert.True(TaxiRateRules.Done(t));
        t.Routes.Add(new TaxiRoute { RouteIndex = 2, FromName = "A", ToName = "C", Fare = 0, TripType = "OneWay" });
        Assert.False(TaxiRateRules.Done(t));
        Assert.NotNull(TaxiRateRules.Error(new UpdateTaxiLocalRateRequest()));
        Assert.NotNull(TaxiRateRules.Error(new UpdateTaxiLocalRateRequest { DayRate = 1800 }));
        Assert.Null(TaxiRateRules.Error(new UpdateTaxiLocalRateRequest { DayRate = 1800, KmPerDay = 80 }));
    }

    [Fact]
    public async Task Jobs_by_atlas_city_use_the_city_centre_and_label_a_far_job_without_a_cutoff()
    {
        var rows = new List<NearbyRow> { new(Guid.NewGuid(), 3000), new(Guid.NewGuid(), 420_000) };
        var q = new FakeNearbyQuery(rows);
        var cat = new FakeCatalog(true, CityFixture.Haldwani);
        StayJobFeedHandlers.Hydrate hydrate = (page, centre) =>
        {
            var map = new Dictionary<Guid, StayJobFeedItemDto>();
            foreach (var r in page)
            {
                var item = new StayJobFeedItemDto { Id = r.Id, Title = "Cook" };
                StayJobFeedHydrator.Fill(item, r.DistanceMeters, null, centre);
                map[r.Id] = item;
            }
            return Task.FromResult(map);
        };

        var res = CityFixture.Read(await StayJobFeedHandlers.BuildAsync(null, null, CityFixture.Haldwani.CityKey, null, null, null, null, null, null,
            q, new FakeNearbyContext(), cat, hydrate));

        Assert.Equal(200, res.Status);
        Assert.Equal(CityFixture.Haldwani.Latitude, q.Calls[0].Lat);
        var items = res.Data.GetProperty("Items");
        Assert.Equal(2, items.GetArrayLength());
        Assert.Equal("near", items[0].GetProperty("LabelKey").GetString());
        Assert.Equal("far", items[1].GetProperty("LabelKey").GetString());
    }

    [Fact]
    public async Task Home_feed_returns_stays_and_taxis_near_the_user_and_an_empty_system_is_not_an_error()
    {
        var memory = new MemoryCache(new MemoryCacheOptions());
        memory.Set(AppTabCache.Key, new List<AppTabDto>());
        HomeNearbyHandlers.Loaders Loaders() => new(
            page => Task.FromResult(page.ToDictionary(r => r.Id, r => new StayFeedItemDto { Id = r.Id, Name = "Stay", DistanceKm = NearbyRules.StayKm(r.DistanceMeters) })),
            page => Task.FromResult(page.ToDictionary(r => r.Id, r => new TaxiFeedItemDto { Id = r.Id, DriverName = "D", DistanceKm = NearbyRules.TaxiKm(r.DistanceMeters) })),
            take => Task.FromResult(new List<StayBrowseDto>()));
        var some = Enumerable.Range(1, 8).Select(i => new NearbyRow(Guid.NewGuid(), i * 1000.0)).ToList();

        var full = CityFixture.Read(await HomeNearbyHandlers.BuildAsync(29.2, 79.5, null, null, null, CityFixture.Uow().Uow, memory,
            new FakeNearbyQuery(some), new FakeNearbyContext(), Loaders()));
        var empty = CityFixture.Read(await HomeNearbyHandlers.BuildAsync(29.2, 79.5, null, null, null, CityFixture.Uow().Uow, memory,
            new FakeNearbyQuery([]), new FakeNearbyContext(), Loaders()));

        Assert.Equal(200, full.Status);
        Assert.Equal(5, full.Data.GetProperty("Stays").GetArrayLength());
        Assert.Equal(5, full.Data.GetProperty("Taxis").GetArrayLength());
        Assert.Equal(200, empty.Status);
        Assert.Equal(0, empty.Data.GetProperty("Stays").GetArrayLength());
    }
}
