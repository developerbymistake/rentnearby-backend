using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RentNearBy.Infrastructure.Services;
using StackExchange.Redis;

namespace RentNearBy.Tests.Trips;

public sealed class RecordingMapPacks : ITripMapPackService
{
    public int Invalidated;
    public List<TripMapPackInfo> Packs { get; set; } = [];
    public Action? OnInvalidate;

    public Task<IReadOnlyList<TripMapPackInfo>> ListAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<TripMapPackInfo>>(Packs);

    public Task<(TripMapPackBytes? Pack, bool Unavailable)> GetAsync(Guid stateId, string version, CancellationToken ct = default)
        => Task.FromResult<(TripMapPackBytes?, bool)>((null, false));

    public bool PacksKnown { get; set; } = true;

    public void Invalidate()
    {
        Interlocked.Increment(ref Invalidated);
        OnInvalidate?.Invoke();
    }
}

public sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
}

// Binary-safe Redis stand-in (the pack bytes are gzip); counts every call.
public class BinaryRedis : DispatchProxy
{
    public ConcurrentDictionary<string, RedisValue> Store = new();
    public int Gets;
    public int Sets;
    public bool Fail;
    private BinaryRedis _owner = null!;
    private IDatabase _db = null!;

    public static (BinaryRedis State, IConnectionMultiplexer Mux) Create()
    {
        var db = DispatchProxy.Create<IDatabase, BinaryRedis>();
        var mux = DispatchProxy.Create<IConnectionMultiplexer, BinaryRedis>();
        var state = (BinaryRedis)(object)db;
        state._owner = state;
        state._db = db;
        ((BinaryRedis)(object)mux)._owner = state;
        ((BinaryRedis)(object)mux)._db = db;
        return (state, mux);
    }

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        var o = _owner;
        var name = method!.Name;
        if (name == "GetDatabase") return _db;
        if (o.Fail && name.EndsWith("Async")) throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down");
        switch (name)
        {
            case "StringGetAsync" when args![0] is RedisKey key:
                Interlocked.Increment(ref o.Gets);
                return Task.FromResult(o.Store.TryGetValue(key.ToString(), out var v) ? v : RedisValue.Null);
            case "StringSetAsync" when args![0] is RedisKey key:
            {
                Interlocked.Increment(ref o.Sets);
                var value = (RedisValue)args[1]!;
                var when = args.Skip(2).OfType<When>().FirstOrDefault();
                if (when == When.NotExists) return Task.FromResult(o.Store.TryAdd(key.ToString(), value));
                o.Store[key.ToString()] = value;
                return Task.FromResult(true);
            }
            case "KeyDeleteAsync" when args![0] is RedisKey key:
                return Task.FromResult(o.Store.TryRemove(key.ToString(), out _));
            case "KeyDeleteAsync" when args![0] is RedisKey[] keys:
                return Task.FromResult((long)keys.Count(k => o.Store.TryRemove(k.ToString(), out _)));
        }
        throw new NotSupportedException(name);
    }
}

public class TripMapPackServiceTests
{
    internal static readonly Guid StateA = Guid.Parse("0b6e1f2a-3c4d-4e5f-8a9b-0c1d2e3f4a5b");
    internal static readonly Guid StateB = Guid.Parse("1c7f2a3b-4d5e-4f60-9bac-1d2e3f4a5b6c");
    internal const string V1 = "9f3c0a1b2c3d4e5f";
    internal const string V2 = "0123456789abcdef";

    internal static string PackJson(string version, Guid? state = null)
        => $$"""{"fmt":1,"v":"{{version}}","state":"{{(state ?? StateA):D}}","stateName":"Test State","count":1,"bbox":[75.6,30.4,79.0,33.2],"img":"https://res.cloudinary.com/x/image/upload/","cats":["temple"],"fields":["id","lat","lon","cat","rank","name","cityKey","thumb","nPhotos"],"s":[["osm:n1",32.24316,77.18922,0,1,"A Temple",210045678,"v1/a.jpg",3]]}""";

    internal sealed class PackAtlas : IAtlasClient
    {
        public int ListCalls;
        public int PackCalls;
        public bool ListDown;
        public bool PackDown;
        public Task? PackGate;
        public Task? ListGate;
        public Dictionary<Guid, string> Versions = new() { [StateA] = V1 };
        // Version Atlas actually builds per state (defaults to Versions).
        public Dictionary<Guid, string> Bodies = new();
        public Func<Guid, byte[]?>? RawOverride;

        public bool IsConfigured => true;

        public Task<AtlasCitySpots?> GetCitySpotsAsync(long cityKey, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AtlasCityPage> SearchCitiesAsync(string? q, int page, int pageSize, bool readyOnly = false,
            bool popularOnly = false, Guid? stateId = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AtlasCatalogVersion> GetCatalogVersionAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public async Task<List<TripMapPackInfo>> GetMapPacksAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref ListCalls);
            if (ListGate != null) await ListGate;
            if (ListDown) throw new AtlasUnavailableException("down");
            return await Task.FromResult(Versions.Select(kv => new TripMapPackInfo
            {
                StateId = kv.Key, StateName = "Test State", Version = kv.Value, Count = 1, Bbox = [75.6, 30.4, 79.0, 33.2],
            }).ToList());
        }

        public async Task<byte[]?> GetMapPackRawAsync(Guid stateId, CancellationToken ct = default)
        {
            Interlocked.Increment(ref PackCalls);
            if (PackGate != null) await PackGate;
            if (PackDown) throw new AtlasUnavailableException("down");
            if (RawOverride != null) return RawOverride(stateId);
            if (!Versions.ContainsKey(stateId)) return null;
            var version = Bodies.GetValueOrDefault(stateId) ?? Versions[stateId];
            return Encoding.UTF8.GetBytes(PackJson(version, stateId));
        }
    }

    internal sealed class Provider(IAtlasClient atlas, IConnectionMultiplexer? redis = null) : IServiceProvider
    {
        public object? GetService(Type serviceType)
            => serviceType == typeof(IAtlasClient) ? atlas : serviceType == typeof(IConnectionMultiplexer) ? redis : null;
    }

    internal static TripMapPackService Service(PackAtlas atlas, ManualClock? clock = null, IConnectionMultiplexer? redis = null)
        => new(Options.Create(new TripPlannerOptions()), new Provider(atlas, redis), NullLogger<TripMapPackService>.Instance,
            clock ?? new ManualClock(new DateTimeOffset(2026, 11, 22, 10, 0, 0, TimeSpan.Zero)));

    [Fact]
    public async Task FiftyParallelGets_OneAtlasPackCall_ThenNone_BytesRoundTrip()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var atlas = new PackAtlas { PackGate = gate.Task };
        var service = Service(atlas);
        await service.ListAsync();

        var calls = Enumerable.Range(0, 50).Select(_ => Task.Run(() => service.GetAsync(StateA, V1))).ToList();
        await Task.Delay(100);
        gate.SetResult();
        var results = await Task.WhenAll(calls);

        Assert.Equal(1, atlas.PackCalls);
        Assert.All(results, r => Assert.Equal(V1, r.Pack!.Version));

        var again = await service.GetAsync(StateA, V1);
        Assert.Equal(1, atlas.PackCalls);
        Assert.Equal(PackJson(V1), Encoding.UTF8.GetString(TripMapPackService.Gunzip(again.Pack!.Gzip)));
    }

    [Fact]
    public async Task TwoProcesses_RedisLock_OneAtlasPackCall()
    {
        var (state, mux) = BinaryRedis.Create();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var atlasA = new PackAtlas { PackGate = gate.Task };
        var atlasB = new PackAtlas();
        var a = Service(atlasA, redis: mux);
        var b = Service(atlasB, redis: mux);
        await a.ListAsync();
        await b.ListAsync();

        var first = a.GetAsync(StateA, V1);
        await Task.Delay(50);
        var second = b.GetAsync(StateA, V1);
        await Task.Delay(400);
        gate.SetResult();
        var (ra, rb) = (await first, await second);

        Assert.Equal(1, atlasA.PackCalls + atlasB.PackCalls);
        Assert.Equal(ra.Pack!.Gzip, rb.Pack!.Gzip);
        Assert.True(state.Store.ContainsKey(TripMapPackKeys.Pack(StateA, V1)));
        Assert.False(state.Store.ContainsKey(TripMapPackKeys.Lock(StateA, V1)));
        Assert.True(state.Store.ContainsKey(TripMapPackKeys.Index));
    }

    [Fact]
    public async Task RedisHit_ServesWithoutAtlas()
    {
        var (state, mux) = BinaryRedis.Create();
        state.Store[TripMapPackKeys.Pack(StateA, V1)] = TripMapPackService.Gzip(Encoding.UTF8.GetBytes(PackJson(V1)));
        var atlas = new PackAtlas();

        var result = await Service(atlas, redis: mux).GetAsync(StateA, V1);

        Assert.NotNull(result.Pack);
        Assert.Equal(0, atlas.PackCalls);
    }

    [Fact]
    public async Task StaleVersion_404_AfterExactlyOneForcedRefresh()
    {
        var atlas = new PackAtlas();
        var service = Service(atlas);
        await service.ListAsync();

        var result = await service.GetAsync(StateA, V2);
        Assert.Null(result.Pack);
        Assert.False(result.Unavailable);
        Assert.Equal(2, atlas.ListCalls);

        await service.GetAsync(StateA, V2);
        Assert.Equal(2, atlas.ListCalls);
        Assert.Equal(0, atlas.PackCalls);
    }

    [Fact]
    public async Task NewVersionNotYetInIndex_FoundAfterForcedRefresh()
    {
        var atlas = new PackAtlas();
        var service = Service(atlas);
        await service.ListAsync();
        atlas.Versions[StateA] = V2;

        var result = await service.GetAsync(StateA, V2);

        Assert.Equal(V2, result.Pack!.Version);
        Assert.Equal(2, atlas.ListCalls);
    }

    [Fact]
    public async Task AtlasBuildsAnotherVersion_404_AndIndexUpdated()
    {
        var atlas = new PackAtlas();
        var service = Service(atlas);
        await service.ListAsync();
        atlas.Versions[StateA] = V2;
        atlas.Bodies[StateA] = V2;

        var result = await service.GetAsync(StateA, V1);

        Assert.Null(result.Pack);
        Assert.False(result.Unavailable);
        Assert.Equal(V2, (await service.ListAsync()).Single().Version);
    }

    [Fact]
    public async Task AtlasDown_NoBytes_Unavailable()
    {
        var atlas = new PackAtlas { PackDown = true };
        var service = Service(atlas);
        await service.ListAsync();

        var result = await service.GetAsync(StateA, V1);

        Assert.Null(result.Pack);
        Assert.True(result.Unavailable);
    }

    [Fact]
    public async Task AtlasDown_MemoryBytesOfSameVersion_Served()
    {
        var atlas = new PackAtlas();
        var service = Service(atlas);
        await service.ListAsync();
        await service.GetAsync(StateA, V1);
        atlas.PackDown = true;
        atlas.ListDown = true;
        service.Invalidate();

        var result = await service.GetAsync(StateA, V1);

        Assert.Equal(V1, result.Pack!.Version);
    }

    [Fact]
    public async Task OlderVersionInMemory_NeverServedUnderNewUrl()
    {
        var atlas = new PackAtlas();
        var service = Service(atlas);
        await service.ListAsync();
        await service.GetAsync(StateA, V1);
        atlas.Versions[StateA] = V2;
        atlas.PackDown = true;

        var result = await service.GetAsync(StateA, V2);

        Assert.Null(result.Pack);
        Assert.True(result.Unavailable);
    }

    [Fact]
    public async Task Oversize_Unavailable()
    {
        var big = new byte[TripMapPackKeys.MaxRawBytes + 1];
        var atlas = new PackAtlas { RawOverride = _ => big };
        var service = Service(atlas);
        await service.ListAsync();

        var result = await service.GetAsync(StateA, V1);

        Assert.Null(result.Pack);
        Assert.True(result.Unavailable);
    }

    [Fact]
    public async Task AtlasPack404_NotFound()
    {
        var atlas = new PackAtlas { RawOverride = _ => null };
        var service = Service(atlas);
        await service.ListAsync();

        var result = await service.GetAsync(StateA, V1);

        Assert.Null(result.Pack);
        Assert.False(result.Unavailable);
    }

    [Theory]
    [InlineData("")]
    [InlineData("9F3C0A1B2C3D4E5F")]
    [InlineData("9f3c0a1b2c3d4e5")]
    [InlineData("9f3c0a1b2c3d4e5fa")]
    [InlineData("../../etc/passwd")]
    public async Task BadVersion_NotFound_NoAtlas(string version)
    {
        var atlas = new PackAtlas();

        var result = await Service(atlas).GetAsync(StateA, version);

        Assert.Null(result.Pack);
        Assert.Equal(0, atlas.ListCalls + atlas.PackCalls);
    }

    [Fact]
    public async Task Index_RefreshAtMostOncePer300s()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 22, 10, 0, 0, TimeSpan.Zero));
        var atlas = new PackAtlas();
        var service = Service(atlas, clock);

        await service.ListAsync();
        clock.Now += TimeSpan.FromSeconds(299);
        await service.ListAsync();
        Assert.Equal(1, atlas.ListCalls);

        clock.Now += TimeSpan.FromSeconds(2);
        await service.ListAsync();
        Assert.Equal(2, atlas.ListCalls);
    }

    [Fact]
    public async Task Invalidate_ForcesNextRefresh_KeepsData()
    {
        var atlas = new PackAtlas();
        var service = Service(atlas);
        await service.ListAsync();

        service.Invalidate();
        atlas.ListDown = true;
        var list = await service.ListAsync();

        Assert.Equal(2, atlas.ListCalls);
        Assert.Equal(V1, list.Single().Version);
    }

    // CF-journey D-3: an Invalidate during a running list refresh must not be swallowed by that refresh.
    [Fact]
    public async Task Invalidate_DuringRefresh_NextCallReloads()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var atlas = new PackAtlas { ListGate = gate.Task };
        var service = Service(atlas);

        var first = service.ListAsync();
        await Task.Delay(50);
        service.Invalidate();
        gate.SetResult();
        Assert.Equal(V1, (await first).Single().Version);

        atlas.ListGate = null;
        atlas.Versions[StateA] = V2;
        var list = await service.ListAsync();

        Assert.Equal(2, atlas.ListCalls);
        Assert.Equal(V2, list.Single().Version);
    }

    [Fact]
    public async Task AtlasDown_NothingKnown_EmptyList_RetryAfterGap()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 22, 10, 0, 0, TimeSpan.Zero));
        var atlas = new PackAtlas { ListDown = true };
        var service = Service(atlas, clock);

        Assert.Empty(await service.ListAsync());
        Assert.Empty(await service.ListAsync());
        Assert.Equal(1, atlas.ListCalls);

        clock.Now += TripMapPackService.ForceGap;
        atlas.ListDown = false;
        Assert.Single(await service.ListAsync());
        Assert.Equal(2, atlas.ListCalls);
    }

    [Fact]
    public async Task AtlasDown_RedisIndexCopyUsed()
    {
        var (_, mux) = BinaryRedis.Create();
        var warm = Service(new PackAtlas(), redis: mux);
        await warm.ListAsync();

        var cold = Service(new PackAtlas { ListDown = true }, redis: mux);
        var list = await cold.ListAsync();

        Assert.Equal(V1, list.Single().Version);
    }

    [Fact]
    public async Task RedisDown_MemoryAndAtlasOnly()
    {
        var (state, mux) = BinaryRedis.Create();
        state.Fail = true;
        var atlas = new PackAtlas();
        var service = Service(atlas, redis: mux);

        await service.ListAsync();
        var result = await service.GetAsync(StateA, V1);

        Assert.Equal(V1, result.Pack!.Version);
        Assert.Equal(1, atlas.PackCalls);
    }

    // REVIEW_LOAD L-02
    [Fact]
    public async Task Stale_index_answers_at_once_while_Atlas_hangs()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 22, 10, 0, 0, TimeSpan.Zero));
        var atlas = new PackAtlas();
        var service = Service(atlas, clock);
        await service.ListAsync();
        atlas.ListGate = new TaskCompletionSource().Task;
        clock.Now += TimeSpan.FromSeconds(301);

        var list = await service.ListAsync().WaitAsync(TimeSpan.FromMilliseconds(500));

        Assert.Equal(V1, list.Single().Version);
        Assert.Equal(2, atlas.ListCalls);
    }

    // REVIEW_LOAD L-01
    [Fact]
    public async Task PacksKnown_is_false_until_a_list_was_read()
    {
        var atlas = new PackAtlas { ListDown = true };
        var service = Service(atlas);

        Assert.Empty(await service.ListAsync());
        Assert.False(service.PacksKnown);

        var ok = Service(new PackAtlas());
        await ok.ListAsync();
        Assert.True(ok.PacksKnown);
    }

    // REVIEW_LOAD L-07
    [Fact]
    public async Task Atlas_down_second_get_inside_the_gap_makes_no_Atlas_call()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 22, 10, 0, 0, TimeSpan.Zero));
        var atlas = new PackAtlas { PackDown = true };
        var service = Service(atlas, clock);
        await service.ListAsync();

        Assert.True((await service.GetAsync(StateA, V1)).Unavailable);
        Assert.True((await service.GetAsync(StateA, V1)).Unavailable);
        Assert.Equal(1, atlas.PackCalls);

        clock.Now += TripMapPackService.ForceGap;
        atlas.PackDown = false;
        Assert.Equal(V1, (await service.GetAsync(StateA, V1)).Pack!.Version);
        Assert.Equal(2, atlas.PackCalls);
    }

    // REVIEW_LOAD L-06
    [Fact]
    public void Pack_bytes_live_24_hours_the_index_7_days()
    {
        Assert.Equal(TimeSpan.FromHours(24), TripMapPackKeys.PackTtl);
        Assert.Equal(TimeSpan.FromDays(7), TripMapPackKeys.Ttl);
        var src = File.ReadAllText(Path.Combine(CompletionCounterGuardTests.Root(), "RentNearBy.Infrastructure", "Services", "TripMapPackService.cs"));
        Assert.Contains("WriteAsync(redis, packKey, gzip, TripMapPackKeys.PackTtl)", src);
    }

    [Fact]
    public async Task Consumer_InvalidatesPacksOnce_AfterTheThreeExistingCalls()
    {
        var order = new List<string>();
        var spots = new OrderSpots(order);
        var road = new OrderRoad(order);
        var catalog = new OrderCatalog(order);
        var packs = new RecordingMapPacks { OnInvalidate = () => order.Add("packs") };
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection([new KeyValuePair<string, string?>("RABBITMQ_URL", "amqp://u:p@localhost:5672")])
            .Build();
        var worker = new AtlasCityChangedWorkerService(spots, road, catalog, packs, config, NullLogger<AtlasCityChangedWorkerService>.Instance);

        await worker.InvalidateCityAsync(210000001L);

        Assert.Equal(1, packs.Invalidated);
        Assert.Equal(new[] { "spots", "road", "catalog", "packs" }, order);
    }

    private sealed class OrderSpots(List<string> order) : ITripSpotCache
    {
        public Task<CachedCitySpots?> GetAsync(long cityKey, CancellationToken ct = default) => throw new NotSupportedException();
        public Task InvalidateAsync(long cityKey) { order.Add("spots"); return Task.CompletedTask; }
    }

    private sealed class OrderCatalog(List<string> order) : ITripCityCatalog
    {
        public Task<TripCityCatalogSnapshot?> GetAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(bool Available, TripCityInfo? City)> FindAsync(long cityKey, CancellationToken ct = default) => throw new NotSupportedException();
        public Task InvalidateAsync() { order.Add("catalog"); return Task.CompletedTask; }
    }

    private sealed class OrderRoad(List<string> order) : ITripRoadCache
    {
        public Task<CityRoadMatrix?> GetCityMatrixAsync(long cityKey, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<CityRoadMatrix?> PeekCityMatrixAsync(long cityKey, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<CityRoadMatrix?> ReadCityMatrixAsync(long cityKey, CancellationToken ct = default) => throw new NotSupportedException();
        public Task InvalidateAsync(long cityKey) { order.Add("road"); return Task.CompletedTask; }
        public Task<System.Text.Json.JsonElement?> GetRouteGeometryAsync(IReadOnlyList<(double Lat, double Lon)> points, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<System.Text.Json.JsonElement?[]> GetRouteGeometriesAsync(IReadOnlyList<IReadOnlyList<(double Lat, double Lon)>> routes, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
