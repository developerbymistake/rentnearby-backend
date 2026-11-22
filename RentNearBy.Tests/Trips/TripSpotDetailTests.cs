using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using RentNearBy.Api.Handlers;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services;

namespace RentNearBy.Tests.Trips;

public class TripSpotDetailTests
{
    private const long Key = 210000001L;
    private static readonly Guid UserId = Guid.Parse("7a1b2c3d-0000-4000-8000-000000000001");
    private static readonly DateTime Generated = new(2026, 11, 22, 8, 0, 0, DateTimeKind.Utc);

    private sealed class FakeCatalog(bool available = true, bool found = true) : ITripCityCatalog
    {
        public int FindCalls;
        public Task<TripCityCatalogSnapshot?> GetAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public Task<(bool Available, TripCityInfo? City)> FindAsync(long cityKey, CancellationToken ct = default)
        {
            FindCalls++;
            return Task.FromResult<(bool, TripCityInfo?)>((available, found ? new TripCityInfo { CityKey = cityKey, Name = "Nainital" } : null));
        }

        public Task InvalidateAsync() => Task.CompletedTask;
    }

    private sealed class FakeCache(CachedCitySpots? value) : ITripSpotCache
    {
        public int GetCalls;
        public Task<CachedCitySpots?> GetAsync(long cityKey, CancellationToken ct = default)
        {
            GetCalls++;
            return Task.FromResult(value);
        }

        public Task InvalidateAsync(long cityKey) => Task.CompletedTask;
    }

    private sealed class FakeLimiter(bool allow = true) : IRateLimitService
    {
        public readonly List<(string Key, int Max, TimeSpan Window)> Checks = [];

        public Task<RateLimitResult> CheckAsync(string key, int maxAttempts, TimeSpan window, int permits = 1)
        {
            Checks.Add((key, maxAttempts, window));
            return Task.FromResult(new RateLimitResult(allow, allow ? maxAttempts - 1 : 0, allow ? null : TimeSpan.FromSeconds(42)));
        }

        public Task ReleaseAsync(string key, string token) => Task.CompletedTask;
    }

    private static ClaimsPrincipal User(Guid? id = null)
        => new(new ClaimsIdentity(id == null ? [] : [new Claim(ClaimTypes.NameIdentifier, id.Value.ToString())], "test"));

    private static CachedCitySpots City(string status = TripSpotStatuses.Ready, DateTime? generatedAt = null)
    {
        var dto = new AtlasCitySpots
        {
            CityKey = Key,
            CityName = "Nainital",
            Status = TripSpotStatuses.Ready,
            GeneratedAt = generatedAt ?? Generated,
            Spots =
            [
                new TripSpotDto
                {
                    Id = "wiki:1", Source = "admin", Name = "Naini Lake", DisplayName = "Naini Lake", Description = "Lake",
                    Latitude = 29.39, Longitude = 79.45, Category = "lake", Area = "Mall Road", AltNames = ["Naini Tal"],
                    VisitMinutes = 60, IsFeatured = true, Rank = 1,
                    Images = Enumerable.Range(1, 5).Select(i => $"https://img/p{i}.jpg").ToList(),
                    Gallery = Enumerable.Range(1, 12).Select(i => $"https://img/g{i}.jpg").ToList(),
                },
                new TripSpotDto { Id = "osm:N2", Name = "Snow View", Category = "viewpoint", Images = ["https://img/s.jpg"] },
            ],
        };
        var cached = TripSpotCache.ToCached(Key, dto, DateTime.UtcNow);
        cached.Status = status;
        return cached;
    }

    private static async Task<(int Status, JsonElement? Body, HttpContext Http)> RunAsync(
        CachedCitySpots? cached, string spotId, long cityKey = Key, FakeCatalog? catalog = null, FakeCache? cache = null,
        FakeLimiter? limiter = null, string? ifNoneMatch = null, ClaimsPrincipal? principal = null)
    {
        var http = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        http.Response.Body = new MemoryStream();
        if (ifNoneMatch != null) http.Request.Headers.IfNoneMatch = ifNoneMatch;

        var result = await TripHandlers.GetCitySpot(cityKey, spotId, principal ?? User(UserId), catalog ?? new FakeCatalog(),
            cache ?? new FakeCache(cached), limiter ?? new FakeLimiter(), http);
        await result.ExecuteAsync(http);

        http.Response.Body.Position = 0;
        var text = await new StreamReader(http.Response.Body).ReadToEndAsync();
        return (http.Response.StatusCode, text.Length == 0 ? null : JsonDocument.Parse(text).RootElement.Clone(), http);
    }

    private static string? ErrorType(JsonElement? body) => body?.GetProperty("error").GetProperty("type").GetString();

    [Fact]
    public async Task Found_ReturnsFlatSpotWithGallery_ETag_CacheControl()
    {
        var (status, body, http) = await RunAsync(City(), "wiki:1");

        Assert.Equal(200, status);
        var data = body!.Value.GetProperty("data");
        Assert.Equal("wiki:1", data.GetProperty("id").GetString());
        Assert.Equal("Naini Lake", data.GetProperty("name").GetString());
        Assert.Equal("Mall Road", data.GetProperty("area").GetString());
        Assert.Equal(3, data.GetProperty("images").GetArrayLength());
        Assert.Equal(10, data.GetProperty("gallery").GetArrayLength());
        Assert.Equal("https://img/g1.jpg", data.GetProperty("gallery")[0].GetString());
        Assert.Equal(10, data.GetProperty("galleryCount").GetInt32());
        Assert.Equal(Key, data.GetProperty("cityKey").GetInt64());
        Assert.Equal(Generated, data.GetProperty("generatedAt").GetDateTime().ToUniversalTime());
        Assert.Equal($"\"{Key}-{Generated.Ticks}\"", http.Response.Headers.ETag.ToString());
        Assert.Equal("private, max-age=300", http.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task Found_HasEveryTripSpotDtoField()
    {
        var (_, body, _) = await RunAsync(City(), "wiki:1");
        var data = body!.Value.GetProperty("data");

        foreach (var prop in typeof(TripSpotDto).GetProperties())
        {
            var name = JsonNamingPolicy.CamelCase.ConvertName(prop.Name);
            Assert.True(data.TryGetProperty(name, out _), $"missing {name}");
        }
    }

    [Fact]
    public async Task NoGallery_ReturnsEmptyArray()
    {
        var (status, body, _) = await RunAsync(City(), "osm:N2");

        Assert.Equal(200, status);
        var data = body!.Value.GetProperty("data");
        Assert.Equal(JsonValueKind.Array, data.GetProperty("gallery").ValueKind);
        Assert.Equal(0, data.GetProperty("gallery").GetArrayLength());
        Assert.Equal(0, data.GetProperty("galleryCount").GetInt32());
    }

    [Fact]
    public async Task MatchingIfNoneMatch_Returns304WithoutBody()
    {
        var etag = TripHandlers.SpotETag(Key, Generated);

        var (status, body, http) = await RunAsync(City(), "wiki:1", ifNoneMatch: etag);

        Assert.Equal(304, status);
        Assert.Null(body);
        Assert.Equal(etag, http.Response.Headers.ETag.ToString());
    }

    [Fact]
    public async Task OtherIfNoneMatch_Returns200()
    {
        var (status, _, _) = await RunAsync(City(), "wiki:1", ifNoneMatch: $"\"{Key}-1\"");

        Assert.Equal(200, status);
    }

    [Fact]
    public async Task DeletedSpot_With_OldETag_Is404NotA304()
    {
        var (status, body, _) = await RunAsync(City(), "wiki:gone", ifNoneMatch: TripHandlers.SpotETag(Key, Generated));

        Assert.Equal(404, status);
        Assert.Equal(TripErrorTypes.SpotNotFound, ErrorType(body));
    }

    [Theory]
    [InlineData("wiki:999")]
    [InlineData(" ")]
    [InlineData("")]
    [InlineData("WIKI:1")]
    public async Task UnknownOrBlankId_IsSpotNotFound(string id)
    {
        var (status, body, _) = await RunAsync(City(), id);

        Assert.Equal(404, status);
        Assert.Equal(TripErrorTypes.SpotNotFound, ErrorType(body));
        Assert.Equal("Place not found", body!.Value.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task TooLongId_IsSpotNotFound_WithoutCacheCall()
    {
        var cache = new FakeCache(City());

        var (status, body, _) = await RunAsync(null, new string('x', 65), cache: cache);

        Assert.Equal(404, status);
        Assert.Equal(TripErrorTypes.SpotNotFound, ErrorType(body));
        Assert.Equal(0, cache.GetCalls);
    }

    [Fact]
    public async Task NotReadyCity_IsSpotNotFound()
    {
        var (status, body, _) = await RunAsync(City(TripSpotStatuses.NotReady), "wiki:1");

        Assert.Equal(404, status);
        Assert.Equal(TripErrorTypes.SpotNotFound, ErrorType(body));
    }

    [Fact]
    public async Task CityKeyZero_IsCityNotFound_WithoutCacheCall()
    {
        var cache = new FakeCache(City());

        var (status, body, _) = await RunAsync(null, "wiki:1", cityKey: 0, cache: cache);

        Assert.Equal(404, status);
        Assert.Equal(TripErrorTypes.CityNotFound, ErrorType(body));
        Assert.Equal(0, cache.GetCalls);
    }

    [Fact]
    public async Task CatalogMiss_IsCityNotFound_WithoutCacheCall()
    {
        var cache = new FakeCache(City());

        var (status, body, _) = await RunAsync(null, "wiki:1", catalog: new FakeCatalog(available: true, found: false), cache: cache);

        Assert.Equal(404, status);
        Assert.Equal(TripErrorTypes.CityNotFound, ErrorType(body));
        Assert.Equal(0, cache.GetCalls);
    }

    [Fact]
    public async Task NothingCached_Is503()
    {
        var (status, body, _) = await RunAsync(null, "wiki:1");

        Assert.Equal(503, status);
        Assert.Equal(TripErrorTypes.SpotsUnavailable, ErrorType(body));
        Assert.Equal("Places are not available right now", body!.Value.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task StaleCopy_Is200_WithoutCacheControl()
    {
        var (status, body, http) = await RunAsync(City(TripSpotStatuses.Stale), "wiki:1");

        Assert.Equal(200, status);
        Assert.Equal(10, body!.Value.GetProperty("data").GetProperty("gallery").GetArrayLength());
        Assert.True(string.IsNullOrEmpty(http.Response.Headers.CacheControl.ToString()));
    }

    [Fact]
    public async Task NoGeneratedAt_HasNoETag()
    {
        var city = City();
        city.GeneratedAt = null;

        var (status, _, http) = await RunAsync(city, "wiki:1");

        Assert.Equal(200, status);
        Assert.True(string.IsNullOrEmpty(http.Response.Headers.ETag.ToString()));
    }

    [Fact]
    public async Task RateLimited_Is429_WithRetryAfter_AndNoCacheCall()
    {
        var limiter = new FakeLimiter(allow: false);
        var cache = new FakeCache(City());

        var (status, _, http) = await RunAsync(null, "wiki:1", cache: cache, limiter: limiter);

        Assert.Equal(429, status);
        Assert.Equal("42", http.Response.Headers["Retry-After"].ToString());
        var check = Assert.Single(limiter.Checks);
        Assert.Equal($"trip:spot:{UserId}", check.Key);
        Assert.Equal(120, check.Max);
        Assert.Equal(TimeSpan.FromMinutes(1), check.Window);
        Assert.Equal(0, cache.GetCalls);
    }

    [Fact]
    public async Task NoUser_Is401()
    {
        var limiter = new FakeLimiter();

        var (status, _, _) = await RunAsync(City(), "wiki:1", limiter: limiter, principal: User());

        Assert.Equal(401, status);
        Assert.Empty(limiter.Checks);
    }

    [Fact]
    public async Task Hit_UsesExactlyOneCacheRead()
    {
        var cache = new FakeCache(City());
        var catalog = new FakeCatalog();

        await RunAsync(null, "wiki:1", cache: cache, catalog: catalog);

        Assert.Equal(1, cache.GetCalls);
        Assert.Equal(1, catalog.FindCalls);
    }

    [Theory]
    [InlineData(nameof(TripHandlers.GetCitySpot))]
    [InlineData(nameof(TripHandlers.GetCitySpots))]
    public void SpotReads_TakeNoDatabaseOrAtlasDependency(string method)
    {
        var parameters = typeof(TripHandlers).GetMethod(method, BindingFlags.Public | BindingFlags.Static)!.GetParameters();

        Assert.DoesNotContain(parameters, p => p.ParameterType == typeof(ApplicationDbContext)
            || p.ParameterType == typeof(IAtlasClient)
            || p.ParameterType == typeof(IUnitOfWork)
            || p.ParameterType.Name.EndsWith("Repository", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Detail_IsACopy_CachedSpotStaysWithoutGallery()
    {
        var city = City();
        var cachedSpot = city.Spots[0];

        await RunAsync(city, "wiki:1");

        Assert.Null(cachedSpot.Gallery);
        Assert.Equal(3, cachedSpot.Images.Count);
    }

    [Fact]
    public void From_CopiesEveryField_AndListsAreNewInstances()
    {
        var spot = City().Spots[0];
        var gallery = new List<string> { "https://img/g.jpg" };

        var detail = TripSpotDetailDto.From(spot, gallery, Key, Generated);

        foreach (var prop in typeof(TripSpotDto).GetProperties().Where(p => p.Name is not (nameof(TripSpotDto.Gallery)) and not (nameof(TripSpotDto.GalleryCount))))
        {
            var a = prop.GetValue(spot);
            var b = prop.GetValue(detail);
            if (a is System.Collections.IEnumerable ea and not string)
                Assert.Equal(ea.Cast<object>(), ((System.Collections.IEnumerable)b!).Cast<object>());
            else
                Assert.Equal(a, b);
        }
        Assert.NotSame(spot.Images, detail.Images);
        Assert.NotSame(gallery, detail.Gallery);
        Assert.Equal(1, detail.GalleryCount);
    }
}
