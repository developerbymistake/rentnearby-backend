using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Infrastructure.Services;

namespace RentNearBy.Tests.Trips;

// No Redis registered: every call goes to the fake Atlas.
public class TripSpotCacheTests
{
    private const long Key = 110000001L;

    private sealed class AtlasOnly(IAtlasClient atlas) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(IAtlasClient) ? atlas : null;
    }

    private sealed class FakeAtlas(Func<long, Task<AtlasCitySpots?>> spots, bool configured = true) : IAtlasClient
    {
        public int Calls;
        public bool IsConfigured => configured;

        public Task<AtlasCitySpots?> GetCitySpotsAsync(long cityKey, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            return spots(cityKey);
        }

        public Task<AtlasCityPage> SearchCitiesAsync(string? q, int page, int pageSize, bool readyOnly = false,
            bool popularOnly = false, Guid? stateId = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<AtlasCatalogVersion> GetCatalogVersionAsync(CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private static TripSpotCache Cache(IAtlasClient atlas)
        => new(Options.Create(new TripPlannerOptions()), new AtlasOnly(atlas), NullLogger<TripSpotCache>.Instance);

    private static AtlasCitySpots ReadySpots() => new()
    {
        CityKey = Key,
        CityName = "Nainital",
        TripEnabled = true,
        Status = TripSpotStatuses.Ready,
        GeneratedAt = new DateTime(2026, 11, 2, 10, 0, 0, DateTimeKind.Utc),
        SpotCount = 2,
        Spots =
        [
            new TripSpotDto { Id = "wiki:1", Name = "Naini Lake", Category = "lake", VisitMinutes = 60, Rank = 1 },
            new TripSpotDto { Id = "osm:N2", Name = "Snow View", Category = "viewpoint", VisitMinutes = 45, Rank = 2 },
        ],
    };

    [Fact]
    public async Task Ready_MapsSpotsAndStatus()
    {
        var atlas = new FakeAtlas(_ => Task.FromResult<AtlasCitySpots?>(ReadySpots()));

        var result = await Cache(atlas).GetAsync(Key);

        Assert.NotNull(result);
        Assert.Equal(TripSpotStatuses.Ready, result.Status);
        Assert.Equal(Key, result.CityKey);
        Assert.Equal("Nainital", result.CityName);
        Assert.Equal(new DateTime(2026, 11, 2, 10, 0, 0, DateTimeKind.Utc), result.GeneratedAt);
        Assert.Equal(new[] { "wiki:1", "osm:N2" }, result.Spots.Select(s => s.Id));
    }

    [Fact]
    public async Task NotReady_HasNoSpots()
    {
        var dto = ReadySpots();
        dto.Status = TripSpotStatuses.NotReady;
        var atlas = new FakeAtlas(_ => Task.FromResult<AtlasCitySpots?>(dto));

        var result = await Cache(atlas).GetAsync(Key);

        Assert.NotNull(result);
        Assert.Equal(TripSpotStatuses.NotReady, result.Status);
        Assert.Empty(result.Spots);
        Assert.Null(result.GeneratedAt);
    }

    [Fact]
    public async Task NotFound_IsNotReady()
    {
        var atlas = new FakeAtlas(_ => Task.FromResult<AtlasCitySpots?>(null));

        var result = await Cache(atlas).GetAsync(Key);

        Assert.NotNull(result);
        Assert.Equal(TripSpotStatuses.NotReady, result.Status);
        Assert.Empty(result.Spots);
    }

    [Fact]
    public async Task AtlasFailure_NoStale_ReturnsNull()
    {
        var atlas = new FakeAtlas(_ => throw new AtlasUnavailableException("down"));

        Assert.Null(await Cache(atlas).GetAsync(Key));
    }

    [Fact]
    public async Task NotConfigured_ReturnsNull_WithoutCallingAtlas()
    {
        var atlas = new FakeAtlas(_ => Task.FromResult<AtlasCitySpots?>(ReadySpots()), configured: false);

        Assert.Null(await Cache(atlas).GetAsync(Key));
        Assert.Equal(0, atlas.Calls);
    }

    [Fact]
    public async Task ConcurrentMisses_ShareOneAtlasCall()
    {
        var gate = new TaskCompletionSource<AtlasCitySpots?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var atlas = new FakeAtlas(_ => gate.Task);
        var cache = Cache(atlas);

        var calls = Enumerable.Range(0, 5).Select(_ => cache.GetAsync(Key)).ToList();
        gate.SetResult(ReadySpots());
        var results = await Task.WhenAll(calls);

        Assert.Equal(1, atlas.Calls);
        Assert.All(results, r => Assert.Equal(TripSpotStatuses.Ready, r!.Status));
    }

    [Fact]
    public async Task FinishedLoad_IsNotReused()
    {
        var atlas = new FakeAtlas(_ => Task.FromResult<AtlasCitySpots?>(ReadySpots()));
        var cache = Cache(atlas);

        await cache.GetAsync(Key);
        await cache.GetAsync(Key);

        Assert.Equal(2, atlas.Calls);
    }

    [Fact]
    public async Task Invalidate_WithoutRedis_DoesNotThrow()
    {
        var atlas = new FakeAtlas(_ => Task.FromResult<AtlasCitySpots?>(null));

        await Cache(atlas).InvalidateAsync(Key);
    }

    [Fact]
    public void ToCached_NullSpotsOnReady_BecomesEmpty()
    {
        var dto = ReadySpots();
        dto.Spots = null!;

        var result = TripSpotCache.ToCached(Key, dto, DateTime.UtcNow);

        Assert.Equal(TripSpotStatuses.Ready, result.Status);
        Assert.Empty(result.Spots);
    }
}
