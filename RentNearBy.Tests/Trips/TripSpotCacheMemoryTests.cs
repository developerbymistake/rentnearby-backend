using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RentNearBy.Api.Handlers;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Infrastructure.Services;
using StackExchange.Redis;

namespace RentNearBy.Tests.Trips;

public class TripSpotCacheMemoryTests
{
    private const long Key = 210000001L;
    private static readonly DateTimeOffset Start = new(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);

    private sealed class SpotsAtlas : IAtlasClient
    {
        public int Calls;
        public bool Down;
        public bool IsConfigured => true;

        public Task<AtlasCitySpots?> GetCitySpotsAsync(long cityKey, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            if (Down) throw new AtlasUnavailableException("down");
            return Task.FromResult<AtlasCitySpots?>(new AtlasCitySpots
            {
                CityKey = cityKey,
                CityName = "Nainital",
                Status = TripSpotStatuses.Ready,
                GeneratedAt = new DateTime(2026, 11, 2, 10, 0, 0, DateTimeKind.Utc),
                Spots = Enumerable.Range(1, 45).Select(i => new TripSpotDto { Id = $"wiki:{i}", Name = $"Spot {i}", Rank = i }).ToList(),
            });
        }

        public Task<AtlasCityPage> SearchCitiesAsync(string? q, int page, int pageSize, bool readyOnly = false,
            bool popularOnly = false, Guid? stateId = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AtlasCatalogVersion> GetCatalogVersionAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class Provider(IAtlasClient atlas, IConnectionMultiplexer? redis) : IServiceProvider
    {
        public object? GetService(Type serviceType)
            => serviceType == typeof(IAtlasClient) ? atlas : serviceType == typeof(IConnectionMultiplexer) ? redis : null;
    }

    private static TripSpotCache Cache(IAtlasClient atlas, IConnectionMultiplexer? redis = null, ManualClock? clock = null)
        => new(Options.Create(new TripPlannerOptions()), new Provider(atlas, redis), NullLogger<TripSpotCache>.Instance, clock ?? new ManualClock(Start));

    [Fact]
    public async Task MemoryHit_MakesNoRedisRead()
    {
        var (redis, mux) = BinaryRedis.Create();
        var atlas = new SpotsAtlas();
        var cache = Cache(atlas, mux);

        await cache.GetAsync(Key);
        var reads = redis.Gets;
        for (var i = 0; i < 10; i++) await cache.GetAsync(Key);

        Assert.Equal(reads, redis.Gets);
        Assert.Equal(1, atlas.Calls);
    }

    [Fact]
    public async Task WithRedis_MemoryExpiresAfter300s_ThenOneRedisRead()
    {
        var clock = new ManualClock(Start);
        var (redis, mux) = BinaryRedis.Create();
        var atlas = new SpotsAtlas();
        var cache = Cache(atlas, mux, clock);
        await cache.GetAsync(Key);
        var reads = redis.Gets;

        clock.Now += TimeSpan.FromSeconds(301);
        await cache.GetAsync(Key);
        await cache.GetAsync(Key);

        Assert.Equal(reads + 1, redis.Gets);
        Assert.Equal(1, atlas.Calls);
    }

    [Fact]
    public async Task NoRedis_ManyPages_OneAtlasCall()
    {
        var atlas = new SpotsAtlas();
        var cache = Cache(atlas);

        for (var i = 0; i < 20; i++) await cache.GetAsync(Key);

        Assert.Equal(1, atlas.Calls);
    }

    [Fact]
    public async Task NoRedis_MemoryHonouredForCacheTtl_ThenReload()
    {
        var clock = new ManualClock(Start);
        var atlas = new SpotsAtlas();
        var cache = Cache(atlas, clock: clock);
        await cache.GetAsync(Key);

        clock.Now += TimeSpan.FromHours(47);
        await cache.GetAsync(Key);
        Assert.Equal(1, atlas.Calls);

        clock.Now += TimeSpan.FromHours(2);
        await cache.GetAsync(Key);
        Assert.Equal(2, atlas.Calls);
    }

    [Fact]
    public async Task RedisFailing_MemoryHonoured()
    {
        var clock = new ManualClock(Start);
        var (redis, mux) = BinaryRedis.Create();
        var atlas = new SpotsAtlas();
        var cache = Cache(atlas, mux, clock);
        await cache.GetAsync(Key);

        redis.Fail = true;
        atlas.Down = true;
        clock.Now += TimeSpan.FromHours(1);
        var result = await cache.GetAsync(Key);

        Assert.Equal(TripSpotStatuses.Ready, result!.Status);
        Assert.Equal(1, atlas.Calls);
    }

    [Fact]
    public async Task Invalidate_KeepsStaleKey_FailingAtlasAnswersStale_StoredEntryNotMutated()
    {
        var (redis, mux) = BinaryRedis.Create();
        var atlas = new SpotsAtlas();
        var cache = Cache(atlas, mux);
        var first = await cache.GetAsync(Key);

        await cache.InvalidateAsync(Key);
        Assert.False(redis.Store.ContainsKey(TripCacheKeys.Spots(Key)));
        Assert.True(redis.Store.ContainsKey(TripCacheKeys.Stale(Key)));
        Assert.Equal(0, cache.MemoryCount);

        atlas.Down = true;
        var stale = await cache.GetAsync(Key);

        Assert.NotNull(stale);
        Assert.Equal(TripSpotStatuses.Stale, stale.Status);
        Assert.NotSame(first, stale);
        Assert.Equal(TripSpotStatuses.Ready, first!.Status);
        Assert.Equal(0, cache.MemoryCount);
    }

    [Fact]
    public async Task AtlasDown_50CallsInsideTheGap_OneAtlasCall_AllStale_ThenRetriesAfterTheGap()
    {
        var clock = new ManualClock(Start);
        var (_, mux) = BinaryRedis.Create();
        var atlas = new SpotsAtlas();
        var cache = Cache(atlas, mux, clock);
        await cache.GetAsync(Key);
        await cache.InvalidateAsync(Key);
        atlas.Down = true;

        var answers = new List<CachedCitySpots?>();
        for (var i = 0; i < 50; i++)
        {
            clock.Now += TimeSpan.FromMilliseconds(150);
            answers.Add(await cache.GetAsync(Key));
        }

        Assert.Equal(2, atlas.Calls);
        Assert.All(answers, a => Assert.Equal(TripSpotStatuses.Stale, a!.Status));
        Assert.All(answers, a => Assert.Equal(45, a!.Spots.Count));

        clock.Now += TripSpotCache.FailGap;
        await cache.GetAsync(Key);
        Assert.Equal(3, atlas.Calls);

        atlas.Down = false;
        clock.Now += TripSpotCache.FailGap;
        Assert.Equal(TripSpotStatuses.Ready, (await cache.GetAsync(Key))!.Status);
        Assert.Equal(4, atlas.Calls);
        Assert.Equal(TripSpotStatuses.Ready, (await cache.GetAsync(Key))!.Status);
        Assert.Equal(4, atlas.Calls);
    }

    [Fact]
    public async Task AtlasDown_InvalidateClearsTheGap_NextCallTriesAtlas()
    {
        var clock = new ManualClock(Start);
        var (_, mux) = BinaryRedis.Create();
        var atlas = new SpotsAtlas();
        var cache = Cache(atlas, mux, clock);
        await cache.GetAsync(Key);
        await cache.InvalidateAsync(Key);
        atlas.Down = true;
        await cache.GetAsync(Key);
        Assert.Equal(2, atlas.Calls);

        atlas.Down = false;
        await cache.InvalidateAsync(Key);
        Assert.Equal(TripSpotStatuses.Ready, (await cache.GetAsync(Key))!.Status);
        Assert.Equal(3, atlas.Calls);
    }

    [Fact]
    public async Task Invalidate_NoRedis_DropsMemory_NextReadReloads()
    {
        var atlas = new SpotsAtlas();
        var cache = Cache(atlas);
        await cache.GetAsync(Key);

        await cache.InvalidateAsync(Key);
        await cache.GetAsync(Key);

        Assert.Equal(2, atlas.Calls);
    }

    [Fact]
    public async Task Memory_IsCappedAt500()
    {
        var clock = new ManualClock(Start);
        var atlas = new SpotsAtlas();
        var cache = Cache(atlas, clock: clock);

        for (var i = 0; i < TripSpotCache.MemoryMaxEntries + 5; i++)
        {
            clock.Now += TimeSpan.FromMilliseconds(1);
            await cache.GetAsync(Key + i);
        }

        Assert.Equal(TripSpotCache.MemoryMaxEntries, cache.MemoryCount);
        await cache.GetAsync(Key);
        Assert.Equal(TripSpotCache.MemoryMaxEntries + 6, atlas.Calls);
    }

    [Fact]
    public async Task Peek_UsesMemory_NoRedisRead()
    {
        var (redis, mux) = BinaryRedis.Create();
        var cache = Cache(new SpotsAtlas(), mux);
        await cache.GetAsync(Key);
        var reads = redis.Gets;

        Assert.NotNull(await cache.PeekAsync(Key));
        Assert.Equal(reads, redis.Gets);
    }

    // ── list ETag (B16) ──────────────────────────────────────────────────────

    private sealed class FakeCatalog : ITripCityCatalog
    {
        public Task<TripCityCatalogSnapshot?> GetAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(bool Available, TripCityInfo? City)> FindAsync(long cityKey, CancellationToken ct = default)
            => Task.FromResult<(bool, TripCityInfo?)>((true, new TripCityInfo { CityKey = cityKey, Name = "Nainital" }));
        public Task InvalidateAsync() => Task.CompletedTask;
    }

    private sealed class FixedCache(CachedCitySpots value) : ITripSpotCache
    {
        public Task<CachedCitySpots?> GetAsync(long cityKey, CancellationToken ct = default) => Task.FromResult<CachedCitySpots?>(value);
        public Task InvalidateAsync(long cityKey) => Task.CompletedTask;
    }

    private static CachedCitySpots Cached(string status, DateTime fetchedAt) => new()
    {
        CityKey = Key,
        CityName = "Nainital",
        Status = status,
        FetchedAt = fetchedAt,
        Spots = status == TripSpotStatuses.NotReady ? [] : Enumerable.Range(1, 45).Select(i => new TripSpotDto { Id = $"wiki:{i}" }).ToList(),
    };

    private static async Task<(int Status, HttpContext Http, JsonElement? Body)> RunList(
        CachedCitySpots cached, string? cursor = null, int pageSize = 20, string? ifNoneMatch = null)
    {
        var http = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        http.Response.Body = new MemoryStream();
        if (ifNoneMatch != null) http.Request.Headers.IfNoneMatch = ifNoneMatch;
        var result = await TripHandlers.GetCitySpots(Key, new FakeCatalog(), new FixedCache(cached),
            Options.Create(new TripPlannerOptions()), http, cursor, pageSize);
        await result.ExecuteAsync(http);
        var bytes = ((MemoryStream)http.Response.Body).ToArray();
        return (http.Response.StatusCode, http, bytes.Length == 0 ? null : JsonDocument.Parse(bytes).RootElement.Clone());
    }

    private static readonly DateTime Fetched = new(2026, 11, 22, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task List_Ready_WeakETag_WithOffsetAndSize_NoCache()
    {
        var (status, http, body) = await RunList(Cached(TripSpotStatuses.Ready, Fetched), cursor: "20", pageSize: 20);

        Assert.Equal(200, status);
        Assert.Equal($"W/\"{Key}-{Fetched.Ticks}-20-20\"", http.Response.Headers.ETag.ToString());
        Assert.Equal("private, no-cache", http.Response.Headers.CacheControl.ToString());
        Assert.Equal(20, body!.Value.GetProperty("data").GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task List_IfNoneMatch_304_OtherPageOrFetch_200()
    {
        var cached = Cached(TripSpotStatuses.Ready, Fetched);
        var (_, http, _) = await RunList(cached);
        var etag = http.Response.Headers.ETag.ToString();

        Assert.Equal(304, (await RunList(cached, ifNoneMatch: etag)).Status);
        Assert.Equal(200, (await RunList(cached, cursor: "20", ifNoneMatch: etag)).Status);
        Assert.Equal(200, (await RunList(cached, pageSize: 10, ifNoneMatch: etag)).Status);
        Assert.Equal(200, (await RunList(Cached(TripSpotStatuses.Ready, Fetched.AddSeconds(1)), ifNoneMatch: etag)).Status);
    }

    [Theory]
    [InlineData(TripSpotStatuses.Stale)]
    [InlineData(TripSpotStatuses.NotReady)]
    public async Task List_NotReady_OrStale_NoETag(string status)
    {
        var (code, http, _) = await RunList(Cached(status, Fetched));

        Assert.Equal(200, code);
        Assert.False(http.Response.Headers.ContainsKey("ETag"));
    }

    [Fact]
    public async Task List_NoIfNoneMatch_BodyUnchangedForOldBuilds()
    {
        var (status, _, body) = await RunList(Cached(TripSpotStatuses.Ready, Fetched));

        Assert.Equal(200, status);
        var data = body!.Value.GetProperty("data");
        Assert.True(data.GetProperty("hasMore").GetBoolean());
        Assert.Equal("20", data.GetProperty("nextCursor").GetString());
        Assert.Equal(45, data.GetProperty("total").GetInt32());
    }
}
