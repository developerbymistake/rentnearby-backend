using Microsoft.Extensions.Logging.Abstractions;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Utils;
using Microsoft.Extensions.Options;
using RentNearBy.Infrastructure.Services;

namespace RentNearBy.Tests.Trips;

// No Redis registered: every call goes to the in-process copy or the fake Atlas.
public class TripCityCatalogTests
{
    private static readonly DateTime V1 = new(2026, 11, 3, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime V2 = new(2026, 11, 3, 9, 0, 0, DateTimeKind.Utc);

    private sealed class AtlasOnly(IAtlasClient atlas, ITripSpotCache? spots = null) : IServiceProvider
    {
        public object? GetService(Type serviceType)
            => serviceType == typeof(IAtlasClient) ? atlas : serviceType == typeof(ITripSpotCache) ? spots : null;
    }

    private sealed class FakeSpots : ITripSpotCache
    {
        public int Calls;
        public int Active;
        public int MaxActive;
        public Func<long, Task<CachedCitySpots?>> Get = _ => Task.FromResult<CachedCitySpots?>(null);

        public async Task<CachedCitySpots?> GetAsync(long cityKey, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            var now = Interlocked.Increment(ref Active);
            int max;
            while ((max = MaxActive) < now && Interlocked.CompareExchange(ref MaxActive, now, max) != max) { }
            try { return await Get(cityKey); }
            finally { Interlocked.Decrement(ref Active); }
        }

        public Task InvalidateAsync(long cityKey) => Task.CompletedTask;
    }

    private static CachedCitySpots Spots(params TripSpotDto[] spots) => new() { Spots = spots.ToList() };

    private static TripSpotDto Spot(string id, string? image, bool featured = false)
        => new() { Id = id, Name = id, ImageUrl = image, IsFeatured = featured };

    private const string Raw = "https://res.cloudinary.com/demo/image/upload/v1/a.jpg";
    private const string Thumb = "https://res.cloudinary.com/demo/image/upload/" + CloudinaryThumb.CityCover + "/v1/a.jpg";

    private sealed class FakeAtlas : IAtlasClient
    {
        public int SearchCalls;
        public int VersionCalls;
        public Func<int, int, Task<AtlasCityPage>> Pages = (_, _) => Task.FromResult(Page(V1, 0, false));
        public Func<Task<AtlasCatalogVersion>> Version = () => Task.FromResult(new AtlasCatalogVersion { UpdatedAt = V1, Count = 0 });

        public bool IsConfigured => true;

        public Task<AtlasCitySpots?> GetCitySpotsAsync(long cityKey, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<AtlasCityPage> SearchCitiesAsync(string? q, int page, int pageSize, bool readyOnly = false,
            bool popularOnly = false, Guid? stateId = null, CancellationToken ct = default)
        {
            var call = Interlocked.Increment(ref SearchCalls);
            return Pages(page, call);
        }

        public Task<AtlasCatalogVersion> GetCatalogVersionAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref VersionCalls);
            return Version();
        }
    }

    private static TripCityCatalog Catalog(IAtlasClient atlas, int revalidateSeconds = 300)
        => new(Options.Create(new TripPlannerOptions { CityCatalogRevalidateSeconds = revalidateSeconds }),
            new AtlasOnly(atlas), NullLogger<TripCityCatalog>.Instance);

    private static AtlasCity City(long key, string name, string district = "Dehradun", bool popular = false, string? image = null) => new()
    {
        CityKey = key, Name = name, DistrictId = Guid.NewGuid(), DistrictName = district, StateName = "Uttarakhand",
        Latitude = 30.1, Longitude = 78.3, Status = "Ready", SpotCount = 5, IsTripEnabled = true,
        IsPopularDestination = popular, UpdatedAt = V1, ImageUrl = image,
    };

    private static AtlasCityPage Page(DateTime updatedAt, int count, bool hasMore, params AtlasCity[] items) => new()
    {
        Items = items.ToList(), HasMore = hasMore, CatalogUpdatedAt = updatedAt, CatalogCount = count,
    };

    private static FakeAtlas OnePage(DateTime updatedAt, params AtlasCity[] items)
        => new()
        {
            Pages = (_, _) => Task.FromResult(Page(updatedAt, items.Length, false, items)),
            Version = () => Task.FromResult(new AtlasCatalogVersion { UpdatedAt = updatedAt, Count = items.Length }),
        };

    [Fact]
    public async Task Load_PagesUntilHasMoreFalse()
    {
        var atlas = new FakeAtlas
        {
            Pages = (page, _) => Task.FromResult(page switch
            {
                1 => Page(V1, 9, true, City(3, "Rishikesh"), City(1, "almora")),
                2 => Page(V1, 9, true, City(2, "Nainital")),
                _ => Page(V1, 9, false, City(4, "Haridwar")),
            }),
        };

        var snapshot = await Catalog(atlas).GetAsync();

        Assert.NotNull(snapshot);
        Assert.Equal(3, atlas.SearchCalls);
        Assert.Equal(new[] { "almora", "Haridwar", "Nainital", "Rishikesh" }, snapshot.Cities.Select(c => c.Name));
        Assert.Equal(9, snapshot.Count);
        Assert.Equal(V1, snapshot.UpdatedAt);
        Assert.False(snapshot.IsStale);
        Assert.Equal(TripCityCatalogSnapshot.VersionOf(V1, 9), snapshot.Version);
    }

    [Fact]
    public async Task Load_RestartsOnceWhenCatalogChangesMidWay()
    {
        var atlas = new FakeAtlas
        {
            Pages = (page, call) => Task.FromResult(page == 1
                ? Page(call == 1 ? V1 : V2, 2, true, City(1, "Almora"))
                : Page(V2, 2, false, City(2, "Nainital"))),
        };

        var snapshot = await Catalog(atlas).GetAsync();

        Assert.NotNull(snapshot);
        Assert.Equal(4, atlas.SearchCalls);
        Assert.Equal(V2, snapshot.UpdatedAt);
        Assert.Equal(new[] { "Almora", "Nainital" }, snapshot.Cities.Select(c => c.Name));
    }

    [Fact]
    public async Task Load_DedupesByCityKey()
    {
        var atlas = new FakeAtlas
        {
            Pages = (page, _) => Task.FromResult(page == 1
                ? Page(V1, 2, true, City(1, "Almora"))
                : Page(V1, 2, false, City(1, "Almora copy"), City(2, "Nainital"))),
        };

        var snapshot = await Catalog(atlas).GetAsync();

        Assert.Equal(new[] { "Almora", "Nainital" }, snapshot!.Cities.Select(c => c.Name));
    }

    [Fact]
    public async Task Revalidate_SameVersion_NoReload()
    {
        var atlas = OnePage(V1, City(1, "Almora"));
        var catalog = Catalog(atlas, revalidateSeconds: 0);

        await catalog.GetAsync();
        var second = await catalog.GetAsync();

        Assert.NotNull(second);
        Assert.Equal(1, atlas.VersionCalls);
        Assert.Equal(1, atlas.SearchCalls);
    }

    [Fact]
    public async Task Revalidate_NewVersion_Reloads()
    {
        var atlas = OnePage(V1, City(1, "Almora"));
        var catalog = Catalog(atlas, revalidateSeconds: 0);
        await catalog.GetAsync();

        atlas.Pages = (_, _) => Task.FromResult(Page(V2, 2, false, City(1, "Almora"), City(2, "Nainital")));
        atlas.Version = () => Task.FromResult(new AtlasCatalogVersion { UpdatedAt = V2, Count = 2 });
        var second = await catalog.GetAsync();

        Assert.Equal(2, atlas.SearchCalls);
        Assert.Equal(2, second!.Cities.Count);
        Assert.Equal(TripCityCatalogSnapshot.VersionOf(V2, 2), second.Version);
    }

    [Fact]
    public async Task AtlasDown_NoCopy_ReturnsNull()
    {
        var atlas = new FakeAtlas { Pages = (_, _) => throw new AtlasUnavailableException("down") };

        Assert.Null(await Catalog(atlas).GetAsync());
        Assert.Equal((false, (TripCityInfo?)null), await Catalog(atlas).FindAsync(1));
    }

    [Fact]
    public async Task AtlasDown_WithCopy_ReturnsStaleFlag()
    {
        var atlas = OnePage(V1, City(1, "Almora"));
        var catalog = Catalog(atlas, revalidateSeconds: 0);
        var first = await catalog.GetAsync();

        atlas.Version = () => Task.FromResult(new AtlasCatalogVersion { UpdatedAt = V2, Count = 1 });
        atlas.Pages = (_, _) => throw new AtlasUnavailableException("down");
        var second = await catalog.GetAsync();

        Assert.NotNull(second);
        Assert.True(second.IsStale);
        Assert.False(first!.IsStale);
        Assert.Equal("Almora", Assert.Single(second.Cities).Name);
    }

    [Fact]
    public async Task VersionCheckFails_ReturnsCopy()
    {
        var atlas = OnePage(V1, City(1, "Almora"));
        var catalog = Catalog(atlas, revalidateSeconds: 0);
        await catalog.GetAsync();

        atlas.Version = () => throw new AtlasUnavailableException("down");
        var second = await catalog.GetAsync();

        Assert.NotNull(second);
        Assert.False(second.IsStale);
        Assert.Equal(1, atlas.SearchCalls);
    }

    [Fact]
    public async Task ConcurrentMisses_LoadOnce()
    {
        var gate = new TaskCompletionSource<AtlasCityPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var atlas = new FakeAtlas { Pages = (_, _) => gate.Task };
        var catalog = Catalog(atlas);

        var calls = Enumerable.Range(0, 5).Select(_ => catalog.GetAsync()).ToList();
        gate.SetResult(Page(V1, 1, false, City(1, "Almora")));
        var results = await Task.WhenAll(calls);

        Assert.Equal(1, atlas.SearchCalls);
        Assert.All(results, r => Assert.Equal("Almora", Assert.Single(r!.Cities).Name));
    }

    [Fact]
    public async Task ConcurrentRevalidates_OneVersionCall()
    {
        var atlas = OnePage(V1, City(1, "Almora"));
        var catalog = Catalog(atlas, revalidateSeconds: 0);
        await catalog.GetAsync();

        var gate = new TaskCompletionSource<AtlasCatalogVersion>(TaskCreationOptions.RunContinuationsAsynchronously);
        atlas.Version = () => gate.Task;
        var calls = Enumerable.Range(0, 5).Select(_ => catalog.GetAsync()).ToList();
        gate.SetResult(new AtlasCatalogVersion { UpdatedAt = V1, Count = 1 });
        await Task.WhenAll(calls);

        Assert.Equal(1, atlas.VersionCalls);
        Assert.Equal(1, atlas.SearchCalls);
    }

    [Fact]
    public async Task Invalidate_SameVersion_KeepsCopyWithoutReload()
    {
        var atlas = OnePage(V1, City(1, "Almora"));
        var catalog = Catalog(atlas);
        await catalog.GetAsync();
        await catalog.GetAsync();
        Assert.Equal(0, atlas.VersionCalls);

        await catalog.InvalidateAsync();
        var after = await catalog.GetAsync();

        Assert.NotNull(after);
        Assert.Equal(1, atlas.VersionCalls);
        Assert.Equal(1, atlas.SearchCalls);
    }

    [Fact]
    public async Task Invalidate_NewVersion_Reloads()
    {
        var atlas = OnePage(V1, City(1, "Almora"));
        var catalog = Catalog(atlas);
        await catalog.GetAsync();

        atlas.Pages = (_, _) => Task.FromResult(Page(V2, 1, false, City(2, "Nainital")));
        atlas.Version = () => Task.FromResult(new AtlasCatalogVersion { UpdatedAt = V2, Count = 1 });
        await catalog.InvalidateAsync();
        var after = await catalog.GetAsync();

        Assert.Equal(2, atlas.SearchCalls);
        Assert.Equal("Nainital", Assert.Single(after!.Cities).Name);
    }

    [Fact]
    public async Task Invalidate_DuringReload_DoesNotStoreOldSnapshot()
    {
        var gate = new TaskCompletionSource<AtlasCityPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var atlas = new FakeAtlas { Pages = (_, call) => call == 1 ? gate.Task : Task.FromResult(Page(V2, 1, false, City(2, "Nainital"))) };
        var catalog = Catalog(atlas);

        var loading = catalog.GetAsync();
        await catalog.InvalidateAsync();
        gate.SetResult(Page(V1, 1, false, City(1, "Almora")));
        var first = await loading;
        var second = await catalog.GetAsync();

        Assert.Equal("Almora", Assert.Single(first!.Cities).Name);
        Assert.Equal("Nainital", Assert.Single(second!.Cities).Name);
        Assert.Equal(2, atlas.SearchCalls);
    }

    [Fact]
    public async Task Find_UnknownKey_AvailableTrue_CityNull()
    {
        var catalog = Catalog(OnePage(V1, City(1, "Almora")));

        var (available, city) = await catalog.FindAsync(99);
        var (_, known) = await catalog.FindAsync(1);

        Assert.True(available);
        Assert.Null(city);
        Assert.Equal("Almora", known!.Name);
    }

    [Fact]
    public async Task EmptyCatalog_IsValid()
    {
        var empty = DateTime.SpecifyKind(DateTime.UnixEpoch, DateTimeKind.Utc);
        var snapshot = await Catalog(OnePage(empty)).GetAsync();

        Assert.NotNull(snapshot);
        Assert.Empty(snapshot.Cities);
        Assert.Equal(0, snapshot.Count);
        Assert.Equal("0-0", snapshot.Version);
    }

    [Fact]
    public void Build_StampsFormatAndPopular()
    {
        var snapshot = TripCityCatalog.Build(Page(V1, 2, false), [City(1, "Almora"), City(2, "Nainital", popular: true)]);

        Assert.Equal(TripCityCatalogSnapshot.CurrentFormat, snapshot.Format);
        Assert.True(snapshot.Format >= 3);
        Assert.False(snapshot.Cities.Single(c => c.CityKey == 1).IsPopularDestination);
        Assert.True(snapshot.Cities.Single(c => c.CityKey == 2).IsPopularDestination);
        Assert.Equal(TripCityCatalogSnapshot.VersionOf(V1, 2), snapshot.Version);
    }

    [Fact]
    public void StoredCopyWithoutFormatKey_ReadsAsFormatZero()
    {
        var json = """{"version":"1-1","updatedAt":"2026-11-02T10:00:00Z","count":1,"cities":[]}""";

        var old = System.Text.Json.JsonSerializer.Deserialize<TripCityCatalogSnapshot>(json, TripJson.Options)!;

        Assert.NotEqual(TripCityCatalogSnapshot.CurrentFormat, old.Format);
    }

    [Fact]
    public void StoredCopyRoundTrip_KeepsFormatAndPopular()
    {
        var built = TripCityCatalog.Build(Page(V1, 1, false), [City(1, "Almora", popular: true)]);
        var json = System.Text.Json.JsonSerializer.Serialize(built, TripJson.Options);

        var back = System.Text.Json.JsonSerializer.Deserialize<TripCityCatalogSnapshot>(json, TripJson.Options)!;

        Assert.Equal(TripCityCatalogSnapshot.CurrentFormat, back.Format);
        Assert.True(Assert.Single(back.Cities).IsPopularDestination);
    }

    [Fact]
    public void VersionOf_Format()
        => Assert.Equal("1793613600000-42",
            TripCityCatalogSnapshot.VersionOf(new DateTime(2026, 11, 2, 10, 0, 0, DateTimeKind.Utc), 42));

    private static TripCityCatalog CatalogWith(IAtlasClient atlas, ITripSpotCache spots)
        => new(Options.Create(new TripPlannerOptions { CityCatalogRevalidateSeconds = 300 }),
            new AtlasOnly(atlas, spots), NullLogger<TripCityCatalog>.Instance);

    [Fact]
    public async Task Cover_CityImageWinsAndSkipsSpotCache()
    {
        var spots = new FakeSpots();
        var snapshot = await CatalogWith(OnePage(V1, City(1, "Almora", image: Raw)), spots).GetAsync();

        var c = Assert.Single(snapshot!.Cities);
        Assert.Equal(Thumb, c.CoverUrl);
        Assert.Equal(Raw, c.ImageUrl);
        Assert.Equal(0, spots.Calls);
    }

    [Fact]
    public async Task Cover_NoCityImage_UsesFeaturedSpotThenFirstWithImage()
    {
        var spots = new FakeSpots
        {
            Get = key => Task.FromResult<CachedCitySpots?>(key == 1
                ? Spots(Spot("a", null), Spot("b", Raw), Spot("c", "https://x.test/c.jpg", featured: true))
                : Spots(Spot("a", null), Spot("b", Raw), Spot("c", "https://x.test/c.jpg"))),
        };
        var snapshot = await CatalogWith(OnePage(V1, City(1, "Almora"), City(2, "Nainital")), spots).GetAsync();

        Assert.Equal("https://x.test/c.jpg", snapshot!.Cities.Single(c => c.CityKey == 1).CoverUrl);
        Assert.Equal(Thumb, snapshot.Cities.Single(c => c.CityKey == 2).CoverUrl);
        Assert.Null(snapshot.Cities.Single(c => c.CityKey == 1).ImageUrl);
    }

    [Fact]
    public async Task Cover_NoImageAnywhere_IsNull()
    {
        var spots = new FakeSpots { Get = _ => Task.FromResult<CachedCitySpots?>(Spots(Spot("a", null), Spot("b", " "))) };
        var snapshot = await CatalogWith(OnePage(V1, City(1, "Almora"), City(2, "Nainital")), spots).GetAsync();

        Assert.All(snapshot!.Cities, c => Assert.Null(c.CoverUrl));
    }

    [Fact]
    public async Task Cover_SpotCacheFailureOrMissingCache_IsToleratedPerCity()
    {
        var spots = new FakeSpots
        {
            Get = key => key == 1 ? throw new InvalidOperationException("boom") : Task.FromResult<CachedCitySpots?>(Spots(Spot("a", Raw))),
        };
        var snapshot = await CatalogWith(OnePage(V1, City(1, "Almora"), City(2, "Nainital")), spots).GetAsync();

        Assert.Null(snapshot!.Cities.Single(c => c.CityKey == 1).CoverUrl);
        Assert.Equal(Thumb, snapshot.Cities.Single(c => c.CityKey == 2).CoverUrl);

        var noCache = await Catalog(OnePage(V1, City(3, "Haridwar"))).GetAsync();
        Assert.Null(Assert.Single(noCache!.Cities).CoverUrl);
    }

    [Fact]
    public async Task Cover_NotReadyOrEmptyCities_DoNotHitSpotCache()
    {
        var spots = new FakeSpots();
        var notReady = City(1, "Almora");
        notReady.Status = "NotReady";
        var empty = City(2, "Nainital");
        empty.SpotCount = 0;
        await CatalogWith(OnePage(V1, notReady, empty), spots).GetAsync();

        Assert.Equal(0, spots.Calls);
    }

    [Fact]
    public async Task Cover_ParallelismIsBounded_AndCallersShareOneReload()
    {
        var spots = new FakeSpots
        {
            Get = async _ => { await Task.Delay(20); return Spots(Spot("a", Raw)); },
        };
        var cities = Enumerable.Range(1, 40).Select(i => City(i, "City" + i)).ToArray();
        var atlas = OnePage(V1, cities);
        var catalog = CatalogWith(atlas, spots);

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => catalog.GetAsync()));

        Assert.All(results, r => Assert.Same(results[0], r));
        Assert.Equal(40, spots.Calls);
        Assert.InRange(spots.MaxActive, 2, TripCityCatalog.CoverParallelism);
        Assert.Equal(1, atlas.SearchCalls);
    }

    [Fact]
    public void StoredCopyOfOlderFormat_IsRejected()
    {
        var older = TripCityCatalogSnapshot.CurrentFormat - 1;
        Assert.True(older < TripCityCatalogSnapshot.CurrentFormat);
        Assert.True(TripCityCatalogSnapshot.CurrentFormat >= 3);
    }

    [Fact]
    public void BestSpotImage_PrefersFeatured_ElseFirst_ElseNull()
    {
        Assert.Null(TripCityCatalog.BestSpotImage(null));
        Assert.Null(TripCityCatalog.BestSpotImage([]));
        Assert.Equal("f", TripCityCatalog.BestSpotImage([Spot("a", "a"), Spot("b", "f", true)]));
        Assert.Equal("a", TripCityCatalog.BestSpotImage([Spot("n", null), Spot("a", "a"), Spot("b", "b")]));
    }
}
