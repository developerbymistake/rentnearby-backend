using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RentNearBy.Api.Handlers;
using RentNearBy.Infrastructure.Services;

namespace RentNearBy.Tests.Trips;

// Journeys of the map pack through the real AtlasClient parse, TripMapPackService and handlers, fed with the golden
// bytes produced by Atlas (Fixtures/golden_*.json are byte-identical copies of the Atlas test fixtures).
public class TripMapPackJourneyTests
{
    private static readonly Guid UserId = Guid.Parse("7a1b2c3d-0000-4000-8000-000000000002");
    private static readonly Guid State = Guid.Parse("0b6e1f2a-3c4d-4e5f-8a9b-0c1d2e3f4a5b");
    private const string V1 = "dae37ac8212fb1a5";
    private const string VCover = "ebe0abd518687028";
    private const string VRemoved = "3459e47ffe5e9aff";

    private static string FixtureDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RentNearBy.Tests.csproj"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "Trips", "Fixtures");
    }

    private static string Golden(string name) => File.ReadAllText(Path.Combine(FixtureDir(), $"golden_{name}.json"), Encoding.UTF8);

    private static byte[] GoldenBytes(string name) => File.ReadAllBytes(Path.Combine(FixtureDir(), $"golden_{name}.json"));

    // Serves Atlas envelopes like the real service: list route and pack route, pack file chosen by the test.
    private sealed class AtlasStub : HttpMessageHandler
    {
        public string Current = "pack";
        public bool Down;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Down) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            var path = request.RequestUri!.AbsolutePath;
            var pack = Golden(Current);
            var data = path.EndsWith("/map-packs", StringComparison.Ordinal) ? ListOf(pack) : pack;
            var body = "{\"status\":\"200\",\"code\":200,\"timestamp\":1,\"data\":" + data + ",\"error\":null}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }

        private static string ListOf(string pack)
        {
            using var doc = JsonDocument.Parse(pack);
            var r = doc.RootElement;
            return "{\"packs\":[{\"stateId\":\"" + r.GetProperty("state").GetString() + "\",\"stateName\":\"" + r.GetProperty("stateName").GetString()
                + "\",\"version\":\"" + r.GetProperty("v").GetString() + "\",\"count\":" + r.GetProperty("count").GetInt32()
                + ",\"bbox\":" + r.GetProperty("bbox").GetRawText() + "}]}";
        }
    }

    private sealed class RealAtlas(AtlasClient real) : IAtlasClient
    {
        public int PackCalls;
        public bool IsConfigured => true;
        public Task<AtlasCitySpots?> GetCitySpotsAsync(long cityKey, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AtlasCityPage> SearchCitiesAsync(string? q, int page, int pageSize, bool readyOnly = false,
            bool popularOnly = false, Guid? stateId = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AtlasCatalogVersion> GetCatalogVersionAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<List<TripMapPackInfo>> GetMapPacksAsync(CancellationToken ct = default) => real.GetMapPacksAsync(ct);

        public Task<byte[]?> GetMapPackRawAsync(Guid stateId, CancellationToken ct = default)
        {
            Interlocked.Increment(ref PackCalls);
            return real.GetMapPackRawAsync(stateId, ct);
        }
    }

    private static AtlasClient Client(HttpMessageHandler handler)
        => new(new HttpClient(handler), Options.Create(new AtlasOptions { BaseUrl = "https://atlas.test", ApiKey = "k123" }),
            NullLogger<AtlasClient>.Instance);

    private sealed class Rig
    {
        public readonly AtlasStub Stub = new();
        public readonly RealAtlas Atlas;
        public readonly TripMapPackService Service;
        public readonly ManualClock Clock = new(new DateTimeOffset(2026, 11, 22, 10, 0, 0, TimeSpan.Zero));

        public Rig()
        {
            Atlas = new RealAtlas(Client(Stub));
            Service = new TripMapPackService(Options.Create(new TripPlannerOptions()),
                new TripMapPackServiceTests.Provider(Atlas), NullLogger<TripMapPackService>.Instance, Clock);
        }

        // What the city-changed consumer does after an admin change, then time passes.
        public void AdminChanged(string file)
        {
            Stub.Current = file;
            Service.Invalidate();
            Clock.Now = Clock.Now.AddSeconds(11);
        }
    }

    private sealed class Limiter : IRateLimitService
    {
        public Task<RateLimitResult> CheckAsync(string key, int maxAttempts, TimeSpan window, int permits = 1)
            => Task.FromResult(new RateLimitResult(true, maxAttempts - 1, null));
        public Task ReleaseAsync(string key, string token) => Task.CompletedTask;
    }

    private sealed class Catalog : ITripCityCatalog
    {
        public Task<TripCityCatalogSnapshot?> GetAsync(CancellationToken ct = default) => Task.FromResult<TripCityCatalogSnapshot?>(new TripCityCatalogSnapshot
        {
            Format = TripCityCatalogSnapshot.CurrentFormat, Version = "1793613600000-42", Count = 42,
            UpdatedAt = new DateTime(2026, 11, 3, 8, 0, 0, DateTimeKind.Utc),
        });
        public Task<(bool Available, TripCityInfo? City)> FindAsync(long cityKey, CancellationToken ct = default) => throw new NotSupportedException();
        public Task InvalidateAsync() => Task.CompletedTask;
    }

    private sealed class Flag(bool on) : IMapDiscoveryFeatureReader
    {
        public Task<bool> IsEnabledAsync() => Task.FromResult(on);
        public void Invalidate() { }
    }

    private sealed class FixedHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }

    private static ClaimsPrincipal User() => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, UserId.ToString())], "test"));

    private static DefaultHttpContext Http(string? acceptEncoding = null)
    {
        var http = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        http.Response.Body = new MemoryStream();
        if (acceptEncoding != null) http.Request.Headers.AcceptEncoding = acceptEncoding;
        return http;
    }

    private static async Task<(int Status, byte[] Body, HttpContext Http)> Pack(Rig rig, string version, string? acceptEncoding = "gzip")
    {
        var http = Http(acceptEncoding);
        var result = await TripMapPackHandlers.GetMapPack(State, version, User(), rig.Service, new Limiter(), http);
        await result.ExecuteAsync(http);
        return (http.Response.StatusCode, ((MemoryStream)http.Response.Body).ToArray(), http);
    }

    private static async Task<JsonElement> VersionAnswer(Rig rig, bool flag)
    {
        var http = Http();
        var result = await TripHandlers.GetCitiesVersion(User(), new Catalog(), new Limiter(), http, rig.Service, new Flag(flag));
        await result.ExecuteAsync(http);
        return JsonDocument.Parse(((MemoryStream)http.Response.Body).ToArray()).RootElement.GetProperty("data").Clone();
    }

    [Fact]
    public async Task Golden_ListEnvelope_ParsesToTheVersionAndHeader()
    {
        var body = "{\"status\":\"200\",\"code\":200,\"timestamp\":1,\"data\":" + Golden("packs_list") + ",\"error\":null}";

        var packs = await Client(new FixedHandler(body)).GetMapPacksAsync();

        var p = Assert.Single(packs);
        Assert.Equal(State, p.StateId);
        Assert.Equal(V1, p.Version);
        Assert.Equal(6, p.Count);
        Assert.Equal(new[] { 75.6, 30.4, 79.0, 33.2 }, p.Bbox);
    }

    [Fact]
    public async Task Journey_FirstDownload_GzipAndPlainBodiesAreTheAtlasBytesExactly()
    {
        var rig = new Rig();
        await rig.Service.ListAsync();

        var (status, gz, http) = await Pack(rig, V1);
        var (plainStatus, plain, _) = await Pack(rig, V1, acceptEncoding: null);

        Assert.Equal(200, status);
        Assert.Equal("gzip", http.Response.Headers.ContentEncoding.ToString());
        Assert.Equal(GoldenBytes("pack"), TripMapPackService.Gunzip(gz));
        Assert.Equal(200, plainStatus);
        Assert.Equal(GoldenBytes("pack"), plain);
        using (var doc = System.Text.Json.JsonDocument.Parse(plain))
            Assert.Equal("vm", doc.RootElement.GetProperty("fields").EnumerateArray().Last().GetString());
        Assert.Equal(1, rig.Atlas.PackCalls);
    }

    [Theory]
    [InlineData("pack_cover", VCover)]
    [InlineData("pack_removed", VRemoved)]
    public async Task Journey_AdminChange_NewVersionServed_OldUrlIs404(string file, string newVersion)
    {
        var rig = new Rig();
        await rig.Service.ListAsync();
        Assert.Equal(200, (await Pack(rig, V1)).Status);

        rig.AdminChanged(file);

        var answer = await VersionAnswer(rig, flag: true);
        var pack = answer.GetProperty("packs").EnumerateArray().Single();
        Assert.Equal(newVersion, pack.GetProperty("version").GetString());
        Assert.Equal($"/trips/map-packs/{State:D}/{newVersion}", pack.GetProperty("path").GetString());

        var (status, gz, _) = await Pack(rig, newVersion);
        Assert.Equal(200, status);
        Assert.Equal(GoldenBytes(file), TripMapPackService.Gunzip(gz));
        // The memory entry of the state is replaced by the new version; the old URL is then gone.
        Assert.Equal(404, (await Pack(rig, V1)).Status);
    }

    [Fact]
    public async Task Journey_DeleteOrUnassign_CountDropsInTheVersionAnswer()
    {
        var rig = new Rig();
        await rig.Service.ListAsync();
        Assert.Equal(6, (await VersionAnswer(rig, flag: true)).GetProperty("packs")[0].GetProperty("count").GetInt32());

        rig.AdminChanged("pack_removed");

        Assert.Equal(5, (await VersionAnswer(rig, flag: true)).GetProperty("packs")[0].GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Journey_AtlasDown_AfterFirstDownload_SameVersionStillServed_NewVersionIsNotInvented()
    {
        var rig = new Rig();
        await rig.Service.ListAsync();
        await Pack(rig, V1);

        rig.Stub.Down = true;
        rig.AdminChanged("pack_cover");

        var (status, gz, _) = await Pack(rig, V1);
        Assert.Equal(200, status);
        Assert.Equal(GoldenBytes("pack"), TripMapPackService.Gunzip(gz));
        Assert.Equal(404, (await Pack(rig, VCover)).Status);
        Assert.Equal(V1, (await VersionAnswer(rig, flag: true)).GetProperty("packs")[0].GetProperty("version").GetString());
    }

    [Fact]
    public async Task Journey_AtlasDown_Cold_VersionAnswerStill200WithEmptyPacks()
    {
        var rig = new Rig();
        rig.Stub.Down = true;

        var answer = await VersionAnswer(rig, flag: true);
        var (status, _, _) = await Pack(rig, V1);

        Assert.Equal(0, answer.GetProperty("packs").GetArrayLength());
        Assert.Equal(404, status);
        Assert.Equal(0, rig.Atlas.PackCalls);
    }

    [Fact]
    public async Task Journey_FlagOff_AnswerSaysFalse_PacksAndRouteKeepServing()
    {
        var rig = new Rig();
        await rig.Service.ListAsync();

        var answer = await VersionAnswer(rig, flag: false);
        var (status, gz, _) = await Pack(rig, V1);

        Assert.False(answer.GetProperty("mapDiscovery").GetBoolean());
        Assert.Equal(1, answer.GetProperty("packs").GetArrayLength());
        Assert.Equal(200, status);
        Assert.Equal(GoldenBytes("pack"), TripMapPackService.Gunzip(gz));
    }
}
