using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RentNearBy.Api.Handlers;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services;
using static RentNearBy.Tests.Trips.TripMapPackServiceTests;

namespace RentNearBy.Tests.Trips;

public class TripMapPackHandlerTests
{
    private static readonly Guid UserId = Guid.Parse("7a1b2c3d-0000-4000-8000-000000000002");

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

    private sealed class FakeCatalog(TripCityCatalogSnapshot? snapshot) : ITripCityCatalog
    {
        public Task<TripCityCatalogSnapshot?> GetAsync(CancellationToken ct = default) => Task.FromResult(snapshot);
        public Task<(bool Available, TripCityInfo? City)> FindAsync(long cityKey, CancellationToken ct = default) => throw new NotSupportedException();
        public Task InvalidateAsync() => Task.CompletedTask;
    }

    private sealed class FakeFlag(bool enabled) : IMapDiscoveryFeatureReader
    {
        public bool Enabled = enabled;
        public Task<bool> IsEnabledAsync() => Task.FromResult(Enabled);
        public void Invalidate() { }
    }

    private static ClaimsPrincipal User(Guid? id = null)
        => new(new ClaimsIdentity(id == null ? [] : [new Claim(ClaimTypes.NameIdentifier, id.Value.ToString())], "test"));

    private static DefaultHttpContext Http(string? ifNoneMatch = null, string? acceptEncoding = null)
    {
        var http = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        http.Response.Body = new MemoryStream();
        if (ifNoneMatch != null) http.Request.Headers.IfNoneMatch = ifNoneMatch;
        if (acceptEncoding != null) http.Request.Headers.AcceptEncoding = acceptEncoding;
        return http;
    }

    private static async Task<(int Status, byte[] Body, HttpContext Http)> RunPack(
        string version, ITripMapPackService? service = null, FakeLimiter? limiter = null, string? ifNoneMatch = null,
        string? acceptEncoding = "gzip, deflate, br", ClaimsPrincipal? principal = null)
    {
        var http = Http(ifNoneMatch, acceptEncoding);
        var atlas = new PackAtlas();
        var svc = service ?? Service(atlas);
        await svc.ListAsync();
        var result = await TripMapPackHandlers.GetMapPack(StateA, version, principal ?? User(UserId), svc, limiter ?? new FakeLimiter(), http);
        await result.ExecuteAsync(http);
        return (http.Response.StatusCode, ((MemoryStream)http.Response.Body).ToArray(), http);
    }

    private static string? ErrorType(byte[] body)
        => body.Length == 0 ? null : JsonDocument.Parse(body).RootElement.GetProperty("error").GetProperty("type").GetString();

    [Fact]
    public async Task Pack200_ExactHeaders_GzipBody()
    {
        var (status, body, http) = await RunPack(V1);

        Assert.Equal(200, status);
        Assert.Equal("gzip", http.Response.Headers.ContentEncoding.ToString());
        Assert.Equal("Accept-Encoding", http.Response.Headers.Vary.ToString());
        Assert.Equal("private, max-age=31536000, immutable", http.Response.Headers.CacheControl.ToString());
        Assert.Equal($"\"{V1}\"", http.Response.Headers.ETag.ToString());
        Assert.Equal("application/json", http.Response.ContentType);
        Assert.Equal(PackJson(V1), Encoding.UTF8.GetString(TripMapPackService.Gunzip(body)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("br")]
    [InlineData("gzip;q=0, br")]
    public async Task NoGzipClient_GetsIdenticalJsonUncompressed(string? acceptEncoding)
    {
        var (status, body, http) = await RunPack(V1, acceptEncoding: acceptEncoding);

        Assert.Equal(200, status);
        Assert.False(http.Response.Headers.ContainsKey("Content-Encoding"));
        Assert.Equal(PackJson(V1), Encoding.UTF8.GetString(body));
        Assert.Equal("private, max-age=31536000, immutable", http.Response.Headers.CacheControl.ToString());
    }

    [Theory]
    [InlineData("\"9f3c0a1b2c3d4e5f\"")]
    [InlineData("W/\"9f3c0a1b2c3d4e5f\"")]
    [InlineData("\"other\", \"9f3c0a1b2c3d4e5f\"")]
    public async Task IfNoneMatch_304(string ifNoneMatch)
    {
        var (status, body, http) = await RunPack(V1, ifNoneMatch: ifNoneMatch);

        Assert.Equal(304, status);
        Assert.Empty(body);
        Assert.Equal($"\"{V1}\"", http.Response.Headers.ETag.ToString());
    }

    [Fact]
    public async Task BadVersion_400()
    {
        var (status, body, _) = await RunPack("NOT-HEX");

        Assert.Equal(400, status);
        Assert.Equal("BadRequest", ErrorType(body));
    }

    [Fact]
    public async Task NotCurrentVersion_404_MapPackNotFound()
    {
        var (status, body, _) = await RunPack(V2);

        Assert.Equal(404, status);
        Assert.Equal(MapPackErrorTypes.MapPackNotFound, ErrorType(body));
    }

    [Fact]
    public async Task AtlasDown_503_MapPackUnavailable()
    {
        var (status, body, _) = await RunPack(V1, service: Service(new PackAtlas { PackDown = true }));

        Assert.Equal(503, status);
        Assert.Equal(MapPackErrorTypes.MapPackUnavailable, ErrorType(body));
    }

    [Fact]
    public async Task RateLimited_429_WithRetryAfter_KeyAndLimit()
    {
        var limiter = new FakeLimiter(allow: false);
        var (status, _, http) = await RunPack(V1, limiter: limiter);

        Assert.Equal(429, status);
        Assert.Equal("42", http.Response.Headers["Retry-After"].ToString());
        Assert.Equal(($"trip:pack:{UserId}", 30, TimeSpan.FromMinutes(1)), limiter.Checks.Single());
    }

    [Fact]
    public async Task NoUser_401()
    {
        var (status, _, _) = await RunPack(V1, principal: User());

        Assert.Equal(401, status);
    }

    [Fact]
    public void PackHandler_TakesNoDatabaseRepositoryOrAtlasClient()
    {
        var parameters = typeof(TripMapPackHandlers).GetMethod(nameof(TripMapPackHandlers.GetMapPack))!.GetParameters();

        Assert.DoesNotContain(parameters, p => p.ParameterType == typeof(ApplicationDbContext)
            || p.ParameterType == typeof(IAtlasClient)
            || p.ParameterType == typeof(IUnitOfWork)
            || p.ParameterType.Name.EndsWith("Repository", StringComparison.Ordinal));
    }

    // ── /trips/cities/version ────────────────────────────────────────────────

    private static TripCityCatalogSnapshot Snapshot() => new()
    {
        Format = TripCityCatalogSnapshot.CurrentFormat,
        Version = "1793613600000-42",
        Count = 42,
        UpdatedAt = new DateTime(2026, 11, 3, 8, 0, 0, DateTimeKind.Utc),
    };

    private static List<TripMapPackInfo> Packs(string version = V1) =>
    [
        new TripMapPackInfo { StateId = StateA, StateName = "Test State", Version = version, Count = 508, Bbox = [75.6, 30.4, 79.0, 33.2] },
    ];

    private static async Task<(int Status, JsonElement? Body, HttpContext Http)> RunVersion(
        List<TripMapPackInfo> packs, bool flag, string? ifNoneMatch = null, TripCityCatalogSnapshot? snapshot = null, bool packsKnown = true)
    {
        var http = Http(ifNoneMatch);
        var result = await TripHandlers.GetCitiesVersion(User(UserId), new FakeCatalog(snapshot ?? Snapshot()), new FakeLimiter(), http,
            new RecordingMapPacks { Packs = packs, PacksKnown = packsKnown }, new FakeFlag(flag));
        await result.ExecuteAsync(http);
        var bytes = ((MemoryStream)http.Response.Body).ToArray();
        return (http.Response.StatusCode, bytes.Length == 0 ? null : JsonDocument.Parse(bytes).RootElement.Clone(), http);
    }

    [Fact]
    public async Task Version_CarriesPacksWithPath_AndFlag_OldFieldsUnchanged()
    {
        var (status, body, _) = await RunVersion(Packs(), flag: true);

        Assert.Equal(200, status);
        var data = body!.Value.GetProperty("data");
        Assert.Equal("1793613600000-42", data.GetProperty("version").GetString());
        Assert.Equal(42, data.GetProperty("count").GetInt32());
        Assert.Equal(new DateTime(2026, 11, 3, 8, 0, 0, DateTimeKind.Utc), data.GetProperty("updatedAt").GetDateTime().ToUniversalTime());
        Assert.False(data.GetProperty("stale").GetBoolean());
        Assert.True(data.GetProperty("mapDiscovery").GetBoolean());

        var pack = data.GetProperty("packs").EnumerateArray().Single();
        Assert.Equal(StateA.ToString("D"), pack.GetProperty("stateId").GetString());
        Assert.Equal("Test State", pack.GetProperty("stateName").GetString());
        Assert.Equal(V1, pack.GetProperty("version").GetString());
        Assert.Equal(508, pack.GetProperty("count").GetInt32());
        Assert.Equal(new[] { 75.6, 30.4, 79.0, 33.2 }, pack.GetProperty("bbox").EnumerateArray().Select(e => e.GetDouble()));
        Assert.Equal($"/trips/map-packs/{StateA:D}/{V1}", pack.GetProperty("path").GetString());

        var names = data.EnumerateObject().Select(p => p.Name).ToList();
        Assert.Equal(new[] { "version", "count", "updatedAt", "stale", "packs", "mapDiscovery", "packsKnown" }, names);
        Assert.True(data.GetProperty("packsKnown").GetBoolean());
    }

    // REVIEW_LOAD L-01: "list not known yet" must differ from "no packs", on the wire and in the ETag.
    [Fact]
    public async Task Version_says_packsKnown_false_when_the_list_is_unknown()
    {
        var (status, body, http) = await RunVersion([], flag: true, packsKnown: false);

        Assert.Equal(200, status);
        Assert.False(body!.Value.GetProperty("data").GetProperty("packsKnown").GetBoolean());
        Assert.Equal("W/\"1793613600000-42.u.1\"", http.Response.Headers.ETag.ToString());
    }

    [Fact]
    public async Task Version_NoIndex_EmptyPacks_Still200()
    {
        var (status, body, http) = await RunVersion([], flag: false);

        Assert.Equal(200, status);
        var data = body!.Value.GetProperty("data");
        Assert.Equal(0, data.GetProperty("packs").GetArrayLength());
        Assert.False(data.GetProperty("mapDiscovery").GetBoolean());
        Assert.Equal("W/\"1793613600000-42.0.0\"", http.Response.Headers.ETag.ToString());
    }

    [Fact]
    public async Task Version_ETagMovesWithPackVersionAndWithFlag()
    {
        var (_, _, a) = await RunVersion(Packs(V1), flag: true);
        var (_, _, b) = await RunVersion(Packs(V2), flag: true);
        var (_, _, c) = await RunVersion(Packs(V1), flag: false);

        var ea = a.Response.Headers.ETag.ToString();
        Assert.NotEqual(ea, b.Response.Headers.ETag.ToString());
        Assert.NotEqual(ea, c.Response.Headers.ETag.ToString());
        Assert.StartsWith("W/\"1793613600000-42.", ea);
        Assert.EndsWith(".1\"", ea);
    }

    [Fact]
    public async Task Version_304_OnlyWhenWholeTagMatches()
    {
        var (_, _, first) = await RunVersion(Packs(V1), flag: true);
        var etag = first.Response.Headers.ETag.ToString();

        var (same, _, _) = await RunVersion(Packs(V1), flag: true, ifNoneMatch: etag);
        var (changed, _, _) = await RunVersion(Packs(V2), flag: true, ifNoneMatch: etag);
        var (old, _, _) = await RunVersion(Packs(V1), flag: true, ifNoneMatch: "W/\"1793613600000-42\"");

        Assert.Equal(304, same);
        Assert.Equal(200, changed);
        Assert.Equal(200, old);
    }

    [Fact]
    public void PacksTag_IsEightHex_OrderSensitiveInput()
    {
        var tag = TripHandlers.PacksTag(Packs());

        Assert.Equal(8, tag.Length);
        Assert.True(tag.All(Uri.IsHexDigit));
        Assert.Equal("0", TripHandlers.PacksTag([]));
    }

    [Fact]
    public void MapPackKeys_Shape()
    {
        Assert.Equal("trip:packs:index", TripMapPackKeys.Index);
        Assert.Equal($"trip:pack:{StateA:N}:{V1}", TripMapPackKeys.Pack(StateA, V1));
        Assert.Equal($"trip:pack:{StateA:N}:{V1}:lock", TripMapPackKeys.Lock(StateA, V1));
        Assert.Equal(2 * 1024 * 1024, TripMapPackKeys.MaxRawBytes);
        Assert.Equal(16, TripMapPackKeys.VersionLength);
    }
}
