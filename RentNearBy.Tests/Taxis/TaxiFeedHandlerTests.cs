using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using RentNearBy.Api.Handlers;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Tests.Trips;

namespace RentNearBy.Tests.Taxis;

internal sealed record FDriver(
    string Name, string City, double Lat, double Lng, (double FromLat, double FromLng, double ToLat, double ToLng)[] Routes,
    short Rank = 0, bool Featured = false, string Category = "Sedan")
{
    public Guid Id { get; } = Guid.NewGuid();
}

// In-memory stand-ins: distance from the grid-snapped pin like the real column, same ordering and keyset rules.
internal sealed class FakeFeedQuery(IReadOnlyList<FDriver> drivers) : INearbyFeedQuery
{
    public static double Km(double la1, double lo1, double la2, double lo2)
    {
        double Rad(double d) => d * Math.PI / 180;
        var a = Math.Pow(Math.Sin(Rad(la2 - la1) / 2), 2) + Math.Cos(Rad(la1)) * Math.Cos(Rad(la2)) * Math.Pow(Math.Sin(Rad(lo2 - lo1) / 2), 2);
        return 2 * 6371 * Math.Asin(Math.Sqrt(a));
    }

    private IEnumerable<NearbyRow> Rows(double lat, double lng, NearbyFilter f) =>
        drivers.Where(d => f.Category == null || d.Category == f.Category)
            .Where(d => f.ExcludeIds == null || !f.ExcludeIds.Contains(d.Id))
            .Select(d => { var s = NearbyRules.TaxiGridSnap(d.Lat, d.Lng); return new NearbyRow(d.Id, Km(lat, lng, s.Lat, s.Lng) * 1000); })
            .OrderBy(r => r.DistanceMeters).ThenBy(r => r.Id);

    public Task<IReadOnlyList<NearbyRow>> PageAsync(NearbyKind kind, double lat, double lng, NearbyToken? after, int take, NearbyFilter filter, CancellationToken ct = default)
    {
        Assert.Equal(NearbyKind.Taxi, kind);
        var q = Rows(lat, lng, filter);
        if (after != null) q = q.Where(r => r.DistanceMeters > after.DistanceMeters || (r.DistanceMeters == after.DistanceMeters && r.Id.CompareTo(after.Id) > 0));
        return Task.FromResult<IReadOnlyList<NearbyRow>>(q.Take(take).ToList());
    }

    public Task<IReadOnlyList<NearbyRow>> FeaturedAsync(NearbyKind kind, double lat, double lng, double maxKm, int take, NearbyFilter filter, CancellationToken ct = default)
    {
        var paid = drivers.Where(d => d.Rank > 0 || d.Featured).Select(d => d.Id).ToHashSet();
        var q = Rows(lat, lng, filter).Where(r => paid.Contains(r.Id) && r.DistanceMeters <= maxKm * 1000);
        return Task.FromResult<IReadOnlyList<NearbyRow>>(q.Take(take).ToList());
    }
}

internal sealed class FakeFeedContext : INearbyContext
{
    public static readonly (string Name, string State, double Lat, double Lng)[] Cities =
    [
        ("Haldwani", "Uttarakhand", 29.2183, 79.5130), ("Nainital", "Uttarakhand", 29.3919, 79.4542),
        ("Delhi", "Delhi", 28.6139, 77.2090), ("Dehradun", "Uttarakhand", 30.3165, 78.0322),
    ];

    public Task<NearbyHeaderDto> HeaderAsync(double lat, double lng, double? nearestKm, CancellationToken ct = default)
    {
        var c = Cities.OrderBy(x => FakeFeedQuery.Km(lat, lng, x.Lat, x.Lng)).First();
        return Task.FromResult(new NearbyHeaderDto
        {
            CentreCityName = c.Name, CentreStateName = c.State, CentreCityKm = Math.Round(FakeFeedQuery.Km(lat, lng, c.Lat, c.Lng), 1),
            LivesIn = "We are live in Uttarakhand", FarAway = nearestKm > NearbyRules.NearKm, NearestKm = nearestKm,
        });
    }

    public Task<NearbyCity?> NearestCityAsync(double lat, double lng, CancellationToken ct = default) => Task.FromResult<NearbyCity?>(null);
}

internal static class TaxiFeedFixture
{
    public static TaxiFeedHydrate Hydrate(IReadOnlyList<FDriver> drivers) => rows =>
    {
        var map = new Dictionary<Guid, TaxiFeedItemDto>();
        foreach (var r in rows)
        {
            var d = drivers.First(x => x.Id == r.Id);
            var km = NearbyRules.TaxiKm(r.DistanceMeters);
            map[r.Id] = new TaxiFeedItemDto
            {
                Id = d.Id, DriverName = d.Name, CityName = d.City, VehicleCategory = d.Category, LocalDayRate = 2000, LocalDayKm = 80,
                PriorityRank = d.Rank, IsFeatured = d.Featured, DistanceKm = km,
                LabelKey = NearbyRules.LabelKey(km, d.City, null), IsNear = NearbyRules.IsNear(km),
            };
        }
        return Task.FromResult(map);
    };

    // Both ends within 5 km of a stored route, either direction: the same rule as SearchByRoutePagedAsync.
    public static Func<int, int, Task<(IReadOnlyList<TaxiBrowseDto> Items, bool HasMore)>> OnRoute(
        IReadOnlyList<FDriver> drivers, double fLat, double fLng, double tLat, double tLng)
    {
        bool Near(double a, double b, double c, double d) => FakeFeedQuery.Km(a, b, c, d) <= TaxiLimits.MatchRadiusMeters / 1000;
        var hits = drivers.Where(d => d.Routes.Any(r =>
            (Near(fLat, fLng, r.FromLat, r.FromLng) && Near(tLat, tLng, r.ToLat, r.ToLng))
            || (Near(fLat, fLng, r.ToLat, r.ToLng) && Near(tLat, tLng, r.FromLat, r.FromLng))))
            .Select(d => new TaxiBrowseDto { Id = d.Id, DriverName = d.Name, CityName = d.City, MatchedFare = 1500 }).ToList();
        return (page, size) =>
        {
            var slice = hits.Skip((page - 1) * size).Take(size).ToList();
            return Task.FromResult(((IReadOnlyList<TaxiBrowseDto>)slice, hits.Count > page * size));
        };
    }

    public static Func<Task<TaxiWaysResultDto?>> NoWays() => () => Task.FromResult<TaxiWaysResultDto?>(null);

    public static Func<Task<TaxiWaysResultDto?>> Ways(params FDriver[] passing) => () => Task.FromResult<TaxiWaysResultDto?>(new TaxiWaysResultDto
    {
        AllTheWay = passing.Select(p => new WayDriverDto { TaxiId = p.Id, DriverName = p.Name, CityName = p.City, VehicleCategory = p.Category, ListedFare = 4000, FromName = "A", ToName = "B" }).ToList(),
    });

    public static ClaimsPrincipal User(Guid? id = null) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, (id ?? Guid.NewGuid()).ToString())], "test"));
}

public class TaxiFeedHandlerTests
{
    private static readonly (double Lat, double Lng) Haldwani = (29.2183, 79.5130);
    private static FDriver D(string name, string city, (double Lat, double Lng) p, short rank = 0, string cat = "Sedan") => new(name, city, p.Lat, p.Lng, [], rank, false, cat);

    private static Task<NearbyPageDto<TaxiFeedItemDto>> Local(IReadOnlyList<FDriver> ds, double lat, double lng, int size = 20, NearbyToken? after = null, string? cat = null)
        => TaxiFeedComposer.LocalAsync(new NearbyArgs(lat, lng, size, after), cat, new FakeFeedQuery(ds), new FakeFeedContext(), TaxiFeedFixture.Hydrate(ds));

    [Fact]
    public async Task Pin_far_from_every_taxi_still_lists_the_nearest_with_far_labels()
    {
        var ds = new[] { D("A", "Haldwani", Haldwani), D("B", "Nainital", (29.3919, 79.4542)) };
        var page = await Local(ds, 28.6139, 77.2090);
        Assert.Equal(2, page.Items.Count);
        Assert.All(page.Items, i => { Assert.Equal("far", i.LabelKey); Assert.False(i.IsNear); Assert.True(i.DistanceKm > 15); Assert.True(i.DistanceKm % 5 == 0); });
        Assert.True(page.Header.FarAway);
        Assert.Equal(page.Items[0].DistanceKm, (int?)page.Header.NearestKm);
    }

    [Fact]
    public async Task Order_is_by_distance_only_a_paid_taxi_is_not_moved_up_in_items()
    {
        var near = D("Near", "Haldwani", Haldwani);
        var paid = D("Paid", "Nainital", (29.3919, 79.4542), rank: 2);
        var page = await Local([near, paid], Haldwani.Lat, Haldwani.Lng);
        Assert.Equal([near.Id, paid.Id], page.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task Featured_is_one_slot_only_when_reasonably_near_and_only_on_the_first_page()
    {
        var near = D("Near", "Haldwani", Haldwani);
        var paidNear = D("PaidNear", "Haldwani", (29.2300, 79.5200), rank: 1);
        var paidFar = D("PaidFar", "Dehradun", (30.3165, 78.0322), rank: 3);
        var ds = new[] { near, paidNear, paidFar };
        var first = await Local(ds, Haldwani.Lat, Haldwani.Lng);
        Assert.Equal([paidNear.Id], first.Featured.Select(f => f.Id));
        var small = await Local(ds, Haldwani.Lat, Haldwani.Lng, 1);
        Assert.True(NearbyToken.TryDecode(small.NextToken, out var after));
        var second = await Local(ds, Haldwani.Lat, Haldwani.Lng, 1, after);
        Assert.Empty(second.Featured);
    }

    [Fact]
    public async Task Paging_has_no_gap_and_no_duplicate()
    {
        var ds = Enumerable.Range(0, 7).Select(i => D("T" + i, "Haldwani", (29.20 + i * 0.05, 79.50))).ToList();
        var seen = new List<Guid>();
        NearbyToken? after = null;
        for (var guard = 0; guard < 10; guard++)
        {
            var p = await Local(ds, Haldwani.Lat, Haldwani.Lng, 3, after);
            seen.AddRange(p.Items.Select(i => i.Id));
            if (!p.HasMore) break;
            Assert.True(NearbyToken.TryDecode(p.NextToken, out after));
        }
        Assert.Equal(7, seen.Count);
        Assert.Equal(7, seen.Distinct().Count());
    }

    [Fact]
    public async Task Label_is_inCity_only_for_the_centre_city_within_15_km()
    {
        var page = await Local([D("In", "Haldwani", Haldwani)], 29.2200, 79.5100);
        Assert.Equal("inCity", page.Items[0].LabelKey);
    }

    [Fact]
    public async Task Category_filter_applies()
    {
        var ds = new[] { D("S", "Haldwani", Haldwani, cat: "Sedan"), D("U", "Haldwani", Haldwani, cat: "SUV") };
        var page = await Local(ds, Haldwani.Lat, Haldwani.Lng, cat: "SUV");
        Assert.Equal(["U"], page.Items.Select(i => i.DriverName));
    }

    [Fact]
    public async Task Whole_system_empty_gives_an_empty_list_not_an_error()
    {
        var page = await Local([], Haldwani.Lat, Haldwani.Lng);
        Assert.Empty(page.Items);
        Assert.False(page.HasMore);
        Assert.Null(page.Header.NearestKm);
    }

    [Fact]
    public async Task Items_carry_no_coordinates_and_no_contact_fields()
    {
        var page = await Local([D("A", "Haldwani", Haldwani)], Haldwani.Lat, Haldwani.Lng);
        var json = System.Text.Json.JsonSerializer.Serialize(page.Items[0]).ToLowerInvariant();
        Assert.DoesNotContain("latitude", json);
        Assert.DoesNotContain("longitude", json);
        Assert.DoesNotContain("phone", json);
    }

    // ---- handler guards ----

    private sealed class Deny : IRateLimitService
    {
        public Task<RateLimitResult> CheckAsync(string key, int maxAttempts, TimeSpan window, int permits = 1) =>
            Task.FromResult(new RateLimitResult(false, 0, TimeSpan.FromSeconds(30)));
        public Task ReleaseAsync(string key, string token) => Task.CompletedTask;
    }

    private sealed class Allow : IRateLimitService
    {
        public Task<RateLimitResult> CheckAsync(string key, int maxAttempts, TimeSpan window, int permits = 1) =>
            Task.FromResult(new RateLimitResult(true, maxAttempts, null));
        public Task ReleaseAsync(string key, string token) => Task.CompletedTask;
    }

    private static Task<IResult> Feed(double? lat, double? lng, string? token, IRateLimitService limiter) =>
        TaxiFeedHandlers.GetFeed(lat, lng, null, token, null, null!, null!, null!, limiter, new ClaimsPrincipal(), new DefaultHttpContext(), default);

    [Theory, InlineData(91, 10), InlineData(10, 181), InlineData(double.NaN, 10)]
    public async Task Invalid_location_is_400(double lat, double lng)
        => Assert.Equal(400, CityFixture.Read(await Feed(lat, lng, null, new Allow())).Status);

    [Fact]
    public async Task Missing_location_is_400() => Assert.Equal(400, CityFixture.Read(await Feed(null, null, null, new Allow())).Status);

    [Fact]
    public async Task Bad_page_token_is_400() => Assert.Equal(400, CityFixture.Read(await Feed(29, 79, "not-a-token", new Allow())).Status);

    [Fact]
    public async Task Rate_limited_caller_gets_429() => Assert.Equal(429, CityFixture.Read(await Feed(29, 79, null, new Deny())).Status);

    [Fact]
    public async Task Route_feed_needs_a_logged_in_user()
    {
        var r = await TaxiFeedHandlers.GetRouteFeed(29, 79, 30, 79, null, null, null, null, null!, null!, null!, null!, null!, new Allow(),
            Options.Create(new TaxiWaysOptions()), new ClaimsPrincipal(), new DefaultHttpContext(), default);
        Assert.Equal(401, CityFixture.Read(r).Status);
    }

    [Fact]
    public async Task Route_feed_rejects_an_unknown_tier()
    {
        var r = await TaxiFeedHandlers.GetRouteFeed(29, 79, 30, 79, null, null, "everything", null, null!, null!, null!, null!, null!, new Allow(),
            Options.Create(new TaxiWaysOptions()), TaxiFeedFixture.User(), new DefaultHttpContext(), default);
        Assert.Equal(400, CityFixture.Read(r).Status);
    }
}
