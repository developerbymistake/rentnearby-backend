using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Infrastructure.Services;
using StackExchange.Redis;

namespace RentNearBy.Tests.Trips;

public class TripRoadCacheTests
{
    private const long Key = 110000001L;
    private const double IslandLat = 29.2;

    private sealed class Provider(FakeRedis? redis, IOsrmClient osrm) : IServiceProvider
    {
        public object? GetService(Type serviceType)
            => serviceType == typeof(IConnectionMultiplexer) ? redis?.Connection
                : serviceType == typeof(IOsrmClient) ? osrm
                : null;
    }

    private sealed class FakeSpots(List<TripSpotDto> spots, string status = TripSpotStatuses.Ready) : ITripSpotCache
    {
        public int Invalidated;
        public int Reads;

        public Task<CachedCitySpots?> GetAsync(long cityKey, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Reads);
            return Task.FromResult<CachedCitySpots?>(new CachedCitySpots { CityKey = cityKey, Status = status, Spots = spots });
        }

        public Task InvalidateAsync(long cityKey)
        {
            Interlocked.Increment(ref Invalidated);
            return Task.CompletedTask;
        }
    }

    // Road values are a pure function of the two points; any leg into the island has no route.
    private sealed class FakeOsrm : IOsrmClient
    {
        public bool IsConfigured { get; set; } = true;
        public int MaxTableCoordinates { get; set; } = 100;
        public int DirectCalls;
        public int BlockCalls;
        public int RouteCalls;
        public readonly ConcurrentQueue<int> BlockCoordinates = new();
        public Func<int, bool> FailBlock = _ => false;
        public Task? Gate;

        public static double? Seconds((double Lat, double Lon) a, (double Lat, double Lon) b)
            => Math.Abs(b.Lat - IslandLat) < 1e-9 ? null : GeoMath.HaversineKm(a.Lat, a.Lon, b.Lat, b.Lon) * 97.3 + 41.7;

        public static double? Metres((double Lat, double Lon) a, (double Lat, double Lon) b)
            => Math.Abs(b.Lat - IslandLat) < 1e-9 ? null : GeoMath.HaversineKm(a.Lat, a.Lon, b.Lat, b.Lon) * 1317.9 + 49.95;

        public static RoadMatrix Reference(IReadOnlyList<(double Lat, double Lon)> points)
        {
            var n = points.Count;
            var minutes = new int[n, n];
            var km = new double[n, n];
            var isEstimate = new bool[n, n];
            for (var i = 0; i < n; i++)
            for (var j = 0; j < n; j++)
            {
                if (i == j) continue;
                var s = Seconds(points[i], points[j]);
                var m = Metres(points[i], points[j]);
                if (s == null || m == null) { isEstimate[i, j] = true; continue; }
                minutes[i, j] = RoadUnits.Minutes(s.Value);
                km[i, j] = RoadUnits.Km(m.Value);
            }
            return new RoadMatrix(minutes, km, isEstimate);
        }

        public Task<RoadMatrix?> TableAsync(IReadOnlyList<(double Lat, double Lon)> points, CancellationToken ct = default)
        {
            Interlocked.Increment(ref DirectCalls);
            return Task.FromResult<RoadMatrix?>(IsConfigured && points.Count <= MaxTableCoordinates ? Reference(points) : null);
        }

        public async Task<RoadTable?> TableAsync(IReadOnlyList<(double Lat, double Lon)> points, IReadOnlyList<int> sources,
            IReadOnlyList<int> destinations, CancellationToken ct = default)
        {
            var call = Interlocked.Increment(ref BlockCalls);
            BlockCoordinates.Enqueue(points.Count);
            if (Gate != null) await Gate;
            if (FailBlock(call) || points.Count > MaxTableCoordinates) return null;

            var seconds = new double?[sources.Count, destinations.Count];
            var metres = new double?[sources.Count, destinations.Count];
            for (var i = 0; i < sources.Count; i++)
            for (var j = 0; j < destinations.Count; j++)
            {
                seconds[i, j] = Seconds(points[sources[i]], points[destinations[j]]);
                metres[i, j] = Metres(points[sources[i]], points[destinations[j]]);
            }
            return new RoadTable(seconds, metres);
        }

        public Task<RoadRoute?> RouteAsync(IReadOnlyList<(double Lat, double Lon)> points, CancellationToken ct = default)
        {
            Interlocked.Increment(ref RouteCalls);
            var geometry = JsonDocument.Parse($"{{\"type\":\"LineString\",\"coordinates\":[[{points[0].Lon},{points[0].Lat}]]}}").RootElement.Clone();
            return Task.FromResult<RoadRoute?>(new RoadRoute(10, 1.0, geometry));
        }
    }

    private static List<TripSpotDto> MakeSpots(int count, int island = -1)
        => Enumerable.Range(0, count).Select(i => new TripSpotDto
        {
            Id = $"osm:N{i}",
            Name = $"Spot {i}",
            Latitude = i == island ? IslandLat : 29.30 + (i % 13) * 0.007 + i * 0.0001,
            Longitude = 79.40 + (i / 13) * 0.009,
            Category = "viewpoint",
            VisitMinutes = 30,
        }).ToList();

    private static TripRoadCache Cache(FakeRedis? redis, FakeOsrm osrm, FakeSpots spots, TripPlannerOptions? o = null)
        => new(spots, Options.Create(o ?? new TripPlannerOptions()), new Provider(redis, osrm), NullLogger<TripRoadCache>.Instance);

    private static TripPlannerService Planner(FakeOsrm osrm, ITripRoadCache cache, TripPlannerOptions? o = null)
        => new(osrm, cache, Options.Create(o ?? new TripPlannerOptions()), NullLogger<TripPlannerService>.Instance);

    private static TripCityInfo City() => new() { CityKey = Key, Name = "Nainital", Latitude = 29.38, Longitude = 79.45 };

    private sealed class FakeCatalog : ITripCityCatalog
    {
        public int Invalidated;

        public Task<TripCityCatalogSnapshot?> GetAsync(CancellationToken ct = default) => Task.FromResult<TripCityCatalogSnapshot?>(null);

        public Task<(bool Available, TripCityInfo? City)> FindAsync(long cityKey, CancellationToken ct = default)
            => Task.FromResult<(bool, TripCityInfo?)>((false, null));

        public Task InvalidateAsync()
        {
            Interlocked.Increment(ref Invalidated);
            return Task.CompletedTask;
        }
    }

    private static PlanTripRequest Request(IEnumerable<TripSpotDto> spots) => new()
    {
        Mode = TripPlanModes.Auto,
        Spots = spots.Select(s => new PlanSpotInput
        {
            Id = s.Id, Name = s.Name, Latitude = s.Latitude, Longitude = s.Longitude, Category = s.Category, VisitMinutes = s.VisitMinutes,
        }).ToList(),
    };

    private static (double, double) Point(TripSpotDto s) => (s.Latitude, s.Longitude);

    private static void AssertSame(RoadMatrix expected, RoadMatrix actual)
    {
        Assert.Equal(expected.Minutes, actual.Minutes);
        Assert.Equal(expected.Km, actual.Km);
        Assert.Equal(expected.IsEstimate, actual.IsEstimate);
    }

    [Fact]
    public async Task Slice_EqualsDirectTable_ForSubset()
    {
        var spots = MakeSpots(12, island: 5);
        var cache = Cache(new FakeRedis(), new FakeOsrm(), new FakeSpots(spots));

        var matrix = await cache.GetCityMatrixAsync(Key);

        Assert.NotNull(matrix);
        var subset = new[] { spots[7], spots[5], spots[2], spots[11] };
        var slice = matrix.Slice(subset.Select(s => s.Id).ToList());
        Assert.NotNull(slice);
        AssertSame(FakeOsrm.Reference(subset.Select(Point).ToList()), slice);
        Assert.True(slice.IsEstimate[0, 1]);
    }

    [Fact]
    public async Task Blocks_StayWithinLimit_AndStitchEqualsOneShot()
    {
        var spots = MakeSpots(130, island: 77);
        var osrm = new FakeOsrm { MaxTableCoordinates = 100 };
        var cache = Cache(new FakeRedis(), osrm, new FakeSpots(spots));

        var matrix = await cache.GetCityMatrixAsync(Key);

        Assert.NotNull(matrix);
        Assert.Equal(9, osrm.BlockCalls);
        Assert.All(osrm.BlockCoordinates, c => Assert.True(c <= 100));
        var full = matrix.Slice(spots.Select(s => s.Id).ToList());
        Assert.NotNull(full);
        AssertSame(FakeOsrm.Reference(spots.Select(Point).ToList()), full);
    }

    [Fact]
    public async Task SmallCity_IsOneRequest()
    {
        var osrm = new FakeOsrm();
        var matrix = await Cache(new FakeRedis(), osrm, new FakeSpots(MakeSpots(40))).GetCityMatrixAsync(Key);

        Assert.NotNull(matrix);
        Assert.Equal(1, osrm.BlockCalls);
    }

    [Fact]
    public async Task Built_IsStoredAsCompactJson_WithSpotTtl()
    {
        var redis = new FakeRedis();
        await Cache(redis, new FakeOsrm(), new FakeSpots(MakeSpots(3, island: 2))).GetCityMatrixAsync(Key);

        var (value, ttl) = redis.Store[TripCacheKeys.Matrix(Key)];
        Assert.Equal(TimeSpan.FromHours(48), ttl);
        using var doc = JsonDocument.Parse(value);
        Assert.Equal(1, doc.RootElement.GetProperty("v").GetInt32());
        Assert.Equal(3, doc.RootElement.GetProperty("ids").GetArrayLength());
        Assert.Equal(9, doc.RootElement.GetProperty("sec").GetArrayLength());
        Assert.Equal(9, doc.RootElement.GetProperty("km").GetArrayLength());
        Assert.Equal(-1, doc.RootElement.GetProperty("sec")[2].GetInt32());
        Assert.False(doc.RootElement.TryGetProperty("IsValid", out _));
        Assert.False(redis.Has(TripCacheKeys.MatrixLock(Key)));
    }

    [Fact]
    public async Task CacheHit_PlanMakesNoOsrmCalls()
    {
        var spots = MakeSpots(20);
        var redis = new FakeRedis();
        var osrm = new FakeOsrm();
        var cache = Cache(redis, osrm, new FakeSpots(spots));
        await cache.GetCityMatrixAsync(Key);
        osrm.BlockCalls = 0;

        var result = await Planner(osrm, cache, new TripPlannerOptions { RouteGeometryMaxDays = 0 })
            .PlanAsync(City(), Request(spots.Take(6)));

        Assert.Equal(0, osrm.BlockCalls);
        Assert.Equal(0, osrm.DirectCalls);
        Assert.Equal(0, osrm.RouteCalls);
        Assert.True(result.OsrmAvailable);
        Assert.All(result.Days, d => Assert.False(d.IsEstimate));
    }

    // Finding 6: plans never wait for the city matrix; the build runs once in the background.
    [Fact]
    public async Task ConcurrentPlans_DoNotWaitForBuild_AndBuildOnce()
    {
        var spots = MakeSpots(20);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var osrm = new FakeOsrm { Gate = gate.Task };
        var redis = new FakeRedis();
        var cache = Cache(redis, osrm, new FakeSpots(spots));
        var planner = Planner(osrm, cache, new TripPlannerOptions { RouteGeometryMaxDays = 0 });

        var a = await planner.PlanAsync(City(), Request(spots.Take(5)));
        var b = await planner.PlanAsync(City(), Request(spots.Skip(5).Take(5)));

        Assert.True(a.OsrmAvailable);
        Assert.True(b.OsrmAvailable);
        Assert.Equal(2, osrm.DirectCalls);
        gate.SetResult();
        Assert.NotNull(await cache.GetCityMatrixAsync(Key));
        Assert.Equal(1, osrm.BlockCalls);
        Assert.True(redis.Has(TripCacheKeys.Matrix(Key)));
    }

    [Fact]
    public async Task LockHeldElsewhere_WaiterReadsTheOtherBuild()
    {
        var spots = MakeSpots(4);
        var redis = new FakeRedis();
        redis.Store[TripCacheKeys.MatrixLock(Key)] = ("1", null);
        var built = await Cache(new FakeRedis(), new FakeOsrm(), new FakeSpots(spots)).GetCityMatrixAsync(Key);
        var osrm = new FakeOsrm();
        var cache = Cache(redis, osrm, new FakeSpots(spots));

        var waiting = cache.GetCityMatrixAsync(Key);
        redis.Store[TripCacheKeys.Matrix(Key)] = (JsonSerializer.Serialize(built), null);
        var result = await waiting;

        Assert.NotNull(result);
        Assert.Equal(0, osrm.BlockCalls);
    }

    [Fact]
    public async Task UnknownSpot_FallsBackToDirectTable()
    {
        var spots = MakeSpots(10);
        var osrm = new FakeOsrm();
        var cache = Cache(new FakeRedis(), osrm, new FakeSpots(spots));
        var extra = new TripSpotDto { Id = "admin:new", Name = "New", Latitude = 29.33, Longitude = 79.41, Category = "viewpoint", VisitMinutes = 30 };

        var result = await Planner(osrm, cache).PlanAsync(City(), Request(spots.Take(3).Append(extra)));

        Assert.Equal(1, osrm.DirectCalls);
        Assert.True(result.OsrmAvailable);
    }

    [Fact]
    public async Task OsrmBlockFailure_CachesNoMatrix_RetryFetchesOnlyMissingBlocks()
    {
        var spots = MakeSpots(130);
        var redis = new FakeRedis();
        var osrm = new FakeOsrm { FailBlock = call => call == 4 };
        var cache = Cache(redis, osrm, new FakeSpots(spots));

        Assert.Null(await cache.GetCityMatrixAsync(Key));
        Assert.Equal(4, osrm.BlockCalls);
        Assert.False(redis.Has(TripCacheKeys.Matrix(Key)));
        Assert.False(redis.Has(TripCacheKeys.MatrixLock(Key)));

        var matrix = await cache.GetCityMatrixAsync(Key);

        Assert.NotNull(matrix);
        Assert.Equal(10, osrm.BlockCalls);
        AssertSame(FakeOsrm.Reference(spots.Select(Point).ToList()), matrix.Slice(spots.Select(s => s.Id).ToList())!);
        Assert.DoesNotContain(redis.Store.Keys, k => k.Contains(":blk:"));
    }

    [Fact]
    public async Task ColdCity_PlanUsesDirectTable_AndWarmsInBackground()
    {
        var spots = MakeSpots(20);
        var redis = new FakeRedis();
        var osrm = new FakeOsrm();
        var cache = Cache(redis, osrm, new FakeSpots(spots));

        var result = await Planner(osrm, cache, new TripPlannerOptions { RouteGeometryMaxDays = 0 }).PlanAsync(City(), Request(spots.Take(4)));

        Assert.True(result.OsrmAvailable);
        Assert.Equal(1, osrm.DirectCalls);
        Assert.NotNull(await cache.GetCityMatrixAsync(Key));
        Assert.True(redis.Has(TripCacheKeys.Matrix(Key)));
    }

    // Finding 2: the anchor is one extra matrix row; its legs count in the day and the route starts and ends there.
    [Fact]
    public async Task Anchor_AddsBaseLegs_AndOneDirectTable()
    {
        var spots = MakeSpots(10);
        var osrm = new FakeOsrm();
        var cache = Cache(new FakeRedis(), osrm, new FakeSpots(spots));
        var request = Request(spots.Take(3));
        request.AnchorLatitude = 29.36;
        request.AnchorLongitude = 79.42;

        var result = await Planner(osrm, cache).PlanAsync(City(), request);

        Assert.Equal(1, osrm.DirectCalls);
        var day = Assert.Single(result.Days);
        Assert.Equal(TripAnchor.Id, day.Legs[0].FromId);
        Assert.Equal(TripAnchor.Id, day.Legs[^1].ToId);
        Assert.Equal(day.Legs.Sum(l => l.Minutes), day.DriveMinutes);
        Assert.NotNull(day.RouteGeoJson);
    }

    [Fact]
    public async Task NoRedis_SkipsMatrix_UsesDirectTable()
    {
        var spots = MakeSpots(10);
        var osrm = new FakeOsrm();
        var cache = Cache(null, osrm, new FakeSpots(spots));

        Assert.Null(await cache.GetCityMatrixAsync(Key));
        await Planner(osrm, cache).PlanAsync(City(), Request(spots.Take(3)));

        Assert.Equal(0, osrm.BlockCalls);
        Assert.Equal(1, osrm.DirectCalls);
    }

    [Fact]
    public async Task RedisDown_DegradesToDirectTable()
    {
        var spots = MakeSpots(10);
        var osrm = new FakeOsrm();
        var cache = Cache(new FakeRedis { Fail = true }, osrm, new FakeSpots(spots));

        var result = await Planner(osrm, cache).PlanAsync(City(), Request(spots.Take(3)));

        Assert.Equal(0, osrm.BlockCalls);
        Assert.Equal(1, osrm.DirectCalls);
        Assert.True(result.OsrmAvailable);
        Assert.All(result.Days.Where(d => d.SpotIds.Count >= 2), d => Assert.NotNull(d.RouteGeoJson));
    }

    [Fact]
    public async Task CityChanged_DeletesMatrixAndLock()
    {
        var redis = new FakeRedis();
        var spots = new FakeSpots(MakeSpots(5));
        var cache = Cache(redis, new FakeOsrm(), spots);
        await cache.GetCityMatrixAsync(Key);
        redis.Store[TripCacheKeys.MatrixLock(Key)] = ("1", null);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection([new KeyValuePair<string, string?>("RABBITMQ_URL", "amqp://u:p@localhost:5672")])
            .Build();
        var catalog = new FakeCatalog();
        var worker = new AtlasCityChangedWorkerService(spots, cache, catalog, config, NullLogger<AtlasCityChangedWorkerService>.Instance);

        await worker.InvalidateCityAsync(Key);

        Assert.False(redis.Has(TripCacheKeys.Matrix(Key)));
        Assert.False(redis.Has(TripCacheKeys.MatrixLock(Key)));
        Assert.Equal(1, spots.Invalidated);
        Assert.Equal(1, catalog.Invalidated);
    }

    // Final verify 13: an invalidation during a build drops the old matrix instead of serving it for 48 h.
    [Fact]
    public async Task InvalidateDuringBuild_StaleMatrixIsNotStored()
    {
        var spots = MakeSpots(20);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var osrm = new FakeOsrm { Gate = gate.Task };
        var redis = new FakeRedis();
        var cache = Cache(redis, osrm, new FakeSpots(spots));

        var build = cache.GetCityMatrixAsync(Key);
        for (var i = 0; i < 400 && Volatile.Read(ref osrm.BlockCalls) == 0; i++) await Task.Delay(5);
        Assert.Equal(1, osrm.BlockCalls);
        await cache.InvalidateAsync(Key);
        gate.SetResult();

        Assert.Null(await build);
        Assert.False(redis.Has(TripCacheKeys.Matrix(Key)));
        Assert.True(redis.Has(TripMatrixRules.GenKey(Key)));

        Assert.NotNull(await cache.GetCityMatrixAsync(Key));
        Assert.True(redis.Has(TripCacheKeys.Matrix(Key)));
    }

    // Final verify 14: a city that can never have a matrix is marked once; later plans start no build.
    [Fact]
    public async Task TooManySpots_SkipMarker_PeekStartsNoBuild()
    {
        var redis = new FakeRedis();
        var osrm = new FakeOsrm();
        var spots = new FakeSpots(MakeSpots(12));
        var cache = Cache(redis, osrm, spots, new TripPlannerOptions { CityMatrixMaxSpots = 10 });

        Assert.Null(await cache.GetCityMatrixAsync(Key));
        Assert.Equal(TimeSpan.FromHours(48), redis.Store[TripMatrixRules.SkipKey(Key)].Ttl);
        Assert.Null(await cache.PeekCityMatrixAsync(Key));
        Assert.Null(await cache.PeekCityMatrixAsync(Key));

        Assert.Equal(1, spots.Reads);
        Assert.Equal(0, osrm.BlockCalls);
        Assert.False(redis.Has(TripCacheKeys.MatrixLock(Key)));

        await cache.InvalidateAsync(Key);
        Assert.False(redis.Has(TripMatrixRules.SkipKey(Key)));
    }

    [Fact]
    public async Task NotReadyCity_SkipMarkerLastsUntilTheSpotCacheRetries()
    {
        var redis = new FakeRedis();
        var spots = new FakeSpots(new List<TripSpotDto>(), TripSpotStatuses.NotReady);
        var cache = Cache(redis, new FakeOsrm(), spots);

        Assert.Null(await cache.GetCityMatrixAsync(Key));

        Assert.Equal(TimeSpan.FromMinutes(60), redis.Store[TripMatrixRules.SkipKey(Key)].Ttl);
        Assert.Null(await cache.PeekCityMatrixAsync(Key));
        Assert.Equal(1, spots.Reads);
    }

    [Fact]
    public async Task OsrmFailure_WritesNoSkipMarker()
    {
        var redis = new FakeRedis();
        var cache = Cache(redis, new FakeOsrm { FailBlock = _ => true }, new FakeSpots(MakeSpots(5)));

        Assert.Null(await cache.GetCityMatrixAsync(Key));

        Assert.False(redis.Has(TripMatrixRules.SkipKey(Key)));
    }

    [Fact]
    public void MatrixRules_SkipTtlAndGeneration()
    {
        var o = new TripPlannerOptions();

        Assert.Null(TripMatrixRules.SkipTtl(null, 0, o));
        Assert.Null(TripMatrixRules.SkipTtl(TripSpotStatuses.Ready, 2, o));
        Assert.Null(TripMatrixRules.SkipTtl(TripSpotStatuses.Ready, 300, o));
        Assert.Equal(TimeSpan.FromHours(48), TripMatrixRules.SkipTtl(TripSpotStatuses.Ready, 301, o));
        Assert.Equal(TimeSpan.FromHours(48), TripMatrixRules.SkipTtl(TripSpotStatuses.Ready, 1, o));
        Assert.Equal(TimeSpan.FromMinutes(60), TripMatrixRules.SkipTtl(TripSpotStatuses.NotReady, 0, o));
        Assert.Equal(TimeSpan.FromMinutes(60), TripMatrixRules.SkipTtl(TripSpotStatuses.Stale, 450, o));
        Assert.True(TripMatrixRules.SameGeneration("", ""));
        Assert.False(TripMatrixRules.SameGeneration("", "a1"));
        Assert.False(TripMatrixRules.SameGeneration("a1", null));
        Assert.Equal("trip:matrix:110000001:gen", TripMatrixRules.GenKey(Key));
        Assert.Equal("trip:matrix:110000001:skip", TripMatrixRules.SkipKey(Key));
    }

    [Fact]
    public async Task RouteGeometry_SecondPlanReusesCache()
    {
        var spots = MakeSpots(10);
        var redis = new FakeRedis();
        var osrm = new FakeOsrm();
        var cache = Cache(redis, osrm, new FakeSpots(spots));
        var planner = Planner(osrm, cache);

        var first = await planner.PlanAsync(City(), Request(spots.Take(4)));
        var routed = osrm.RouteCalls;
        var second = await planner.PlanAsync(City(), Request(spots.Take(4)));

        Assert.True(routed > 0);
        Assert.Equal(routed, osrm.RouteCalls);
        var routes = redis.Store.Where(e => e.Key.StartsWith("trip:route:")).ToList();
        Assert.Equal(routed, routes.Count);
        Assert.All(routes, e => Assert.Equal(TimeSpan.FromDays(7), e.Value.Ttl));
        Assert.Equal(first.Days[0].RouteGeoJson!.Value.GetRawText(), second.Days[0].RouteGeoJson!.Value.GetRawText());
    }
}
