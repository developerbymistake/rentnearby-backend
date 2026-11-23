using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RentNearBy.Core.DTOs.Buddy;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Tests.Support.Buddy;

namespace RentNearBy.Tests.Buddy.Be2;

// Bug 15: a joined viewer gets the owner's road line per day, over the day's spots only.
public class TripViewerRoutesTests
{
    private static TripViewerSpotDto Spot(string id, double lat, double lon) => new() { Id = id, Name = id, Latitude = lat, Longitude = lon };

    private static TripViewerDayDto Day(int day, params TripViewerSpotDto[] spots) => new() { Day = day, PlaceCount = spots.Length, Spots = [.. spots] };

    private static List<TripViewerDayDto> Days() =>
    [
        Day(1, Spot("a", 29.39, 79.45), Spot("b", 29.38, 79.46), Spot("c", 29.37, 79.47)),
        Day(2, Spot("d", 29.40, 79.44)),
        Day(3),
        Day(4, Spot("e", 29.35, 79.50), Spot("f", 29.36, 79.51)),
    ];

    [Fact]
    public async Task A_day_with_two_or_more_spots_gets_the_road_line_over_its_spots_only()
    {
        var days = Days();
        var roads = new FakeRoadCache();
        await TripViewerRoutes.FillAsync(days, roads, 14);

        Assert.Equal(2, roads.Asked.Count);
        Assert.Equal(new[] { (29.39, 79.45), (29.38, 79.46), (29.37, 79.47) }, roads.Asked[0]);
        Assert.Equal(new[] { (29.35, 79.50), (29.36, 79.51) }, roads.Asked[1]);
        var line = Assert.IsType<JsonElement>(days[0].RouteGeoJson);
        Assert.Equal("LineString", line.GetProperty("type").GetString());
        Assert.Equal(3, line.GetProperty("coordinates").GetArrayLength());
        Assert.NotNull(days[3].RouteGeoJson);
        Assert.Null(days[1].RouteGeoJson);
        Assert.Null(days[2].RouteGeoJson);
    }

    [Fact]
    public async Task Osrm_down_leaves_every_day_without_a_line()
    {
        var days = Days();
        await TripViewerRoutes.FillAsync(days, new FakeRoadCache { Down = true }, 14);
        Assert.All(days, d => Assert.Null(d.RouteGeoJson));
    }

    [Fact]
    public async Task Osrm_down_through_the_real_road_cache_gives_null()
    {
        var osrm = new DownOsrm();
        var cache = new TripRoadCache(new NoSpots(), Options.Create(new TripPlannerOptions()), new Provider(osrm), NullLogger<TripRoadCache>.Instance);
        var days = Days();
        await TripViewerRoutes.FillAsync(days, cache, 14);
        Assert.Equal(2, osrm.RouteCalls);
        Assert.All(days, d => Assert.Null(d.RouteGeoJson));
    }

    [Fact]
    public async Task The_route_day_cap_is_the_planners_and_counts_routable_days_only()
    {
        var days = Days();
        var roads = new FakeRoadCache();
        await TripViewerRoutes.FillAsync(days, roads, 1);
        Assert.Single(roads.Asked);
        Assert.NotNull(days[0].RouteGeoJson);
        Assert.Null(days[3].RouteGeoJson);

        var none = new FakeRoadCache();
        await TripViewerRoutes.FillAsync(Days(), none, 0);
        Assert.Empty(none.Asked);
    }

    [Fact]
    public async Task No_routable_day_asks_nothing()
    {
        var roads = new FakeRoadCache();
        await TripViewerRoutes.FillAsync([Day(1, Spot("a", 29.39, 79.45)), Day(2)], roads, 14);
        Assert.Empty(roads.Asked);
    }

    private sealed class Provider(IOsrmClient osrm) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(IOsrmClient) ? osrm : null;
    }

    private sealed class NoSpots : ITripSpotCache
    {
        public Task<CachedCitySpots?> GetAsync(long cityKey, CancellationToken ct = default) => Task.FromResult<CachedCitySpots?>(null);
        public Task InvalidateAsync(long cityKey) => Task.CompletedTask;
    }

    // OsrmClient answers null when the server is unreachable or answers 5xx.
    private sealed class DownOsrm : IOsrmClient
    {
        public int RouteCalls;
        public bool IsConfigured => true;
        public int MaxTableCoordinates => 100;
        public Task<RoadMatrix?> TableAsync(IReadOnlyList<(double Lat, double Lon)> points, CancellationToken ct = default) => Task.FromResult<RoadMatrix?>(null);
        public Task<RoadTable?> TableAsync(IReadOnlyList<(double Lat, double Lon)> points, IReadOnlyList<int> sources, IReadOnlyList<int> destinations, CancellationToken ct = default)
            => Task.FromResult<RoadTable?>(null);
        public Task<RoadRoute?> RouteAsync(IReadOnlyList<(double Lat, double Lon)> points, CancellationToken ct = default)
        {
            Interlocked.Increment(ref RouteCalls);
            return Task.FromResult<RoadRoute?>(null);
        }
    }
}
