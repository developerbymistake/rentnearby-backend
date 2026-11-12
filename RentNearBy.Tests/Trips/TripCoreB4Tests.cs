using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RentNearBy.Api.Handlers;
using RentNearBy.Api.Validators;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Infrastructure.Services;

namespace RentNearBy.Tests.Trips;

// B4 trip core: saved guide step in progress, create rate limit and draft cap, covers from cached spot sets only.
public class TripCoreB4Tests
{
    private static TripPlan PlanWithLegs(int legs, TripGuide? guide) => new()
    {
        Guide = guide,
        Legs = Enumerable.Range(1, legs).Select(i => new TripCityLeg { Index = i, CityKey = 210000000 + i, CityName = "C" + i }).ToList(),
    };

    private static Trip NewTrip() => new() { Id = Guid.NewGuid(), CityName = "C1", Status = TripStatuses.Planned };

    // ---- RM-1: GuideStep

    [Fact]
    public void Compute_carries_the_saved_guide_step_while_rows_are_still_todo()
    {
        var plan = PlanWithLegs(2, new TripGuide { Step = "taxi" });
        var progress = TripEasyRows.Compute(NewTrip(), plan, [], null);
        Assert.Equal("taxi", progress.GuideStep);
        // The server's own next row is the first todo row, which is not where Rahul stopped.
        Assert.Equal("gettingThere", progress.Next!.Key);
    }

    [Theory]
    [InlineData("gettingThere")]
    [InlineData("comingBack")]
    [InlineData("stay:2")]
    [InlineData("between:2")]
    public void GuideStep_accepts_every_step_key_of_the_plan(string step)
        => Assert.Equal(step, TripEasyRows.GuideStepOf(PlanWithLegs(2, new TripGuide { Step = step })));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nonsense")]
    [InlineData("stay:0")]
    [InlineData("between:1")]
    [InlineData("stay:3")]
    [InlineData("between:3")]
    public void GuideStep_is_null_for_an_invalid_key_or_a_step_the_plan_no_longer_has(string? step)
        => Assert.Null(TripEasyRows.GuideStepOf(PlanWithLegs(2, new TripGuide { Step = step })));

    [Fact]
    public void GuideStep_is_null_without_a_guide_and_survives_the_plan_json_round_trip()
    {
        Assert.Null(TripEasyRows.GuideStepOf(PlanWithLegs(1, null)));
        var plan = PlanWithLegs(2, new TripGuide { Step = "stay:2", Choices = new() { ["stay:1"] = "find" } });
        var trip = NewTrip();
        trip.PlanJson = System.Text.Json.JsonSerializer.Serialize(plan, TripJson.Options);
        Assert.Equal("stay:2", TripEasyRows.Compute(trip, TripPlanReader.Read(trip), [], null).GuideStep);
    }

    [Fact]
    public void Progress_without_a_guide_step_leaves_it_null()
        => Assert.Null(TripEasyRows.Progress([]).GuideStep);

    [Fact]
    public void StepKeysOf_lists_every_key_of_a_three_city_plan()
        => Assert.Equal(
            new[] { "between:2", "between:3", "comingBack", "gettingThere", "stay:1", "stay:2", "stay:3", "taxi" },
            TripEasyRows.StepKeysOf(PlanWithLegs(3, null)).OrderBy(k => k, StringComparer.Ordinal).ToArray());

    // ---- create limits

    private static ClaimsPrincipal User(Guid id)
        => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id.ToString())], "test"));

    private static Task<IResult> CreateAs(Guid user, IRateLimitService limiter, HttpContext http)
        => TripHandlers.Create(new SaveTripRequest { Plan = null }, new SaveTripRequestValidator(), User(user), null!, null!, null!,
            NullLoggerFactory.Instance, limiter, http, null!);

    [Fact]
    public async Task Create_is_limited_to_30_a_minute_per_user_with_a_retry_after()
    {
        var limiter = new InMemoryRateLimitService();
        var rahul = Guid.NewGuid();
        for (var i = 0; i < 30; i++)
            Assert.Equal(400, ((IStatusCodeHttpResult)await CreateAs(rahul, limiter, new DefaultHttpContext())).StatusCode);

        var http = new DefaultHttpContext();
        var denied = await CreateAs(rahul, limiter, http);
        Assert.Equal(429, ((IStatusCodeHttpResult)denied).StatusCode);
        Assert.True(int.Parse(http.Response.Headers["Retry-After"].ToString()) > 0);

        // Another traveller is not affected.
        Assert.Equal(400, ((IStatusCodeHttpResult)await CreateAs(Guid.NewGuid(), limiter, new DefaultHttpContext())).StatusCode);
    }

    [Fact]
    public void Draft_cap_blocks_at_the_limit_and_not_below()
    {
        Assert.False(TripCreateRules.DraftCapReached(TripCreateRules.MaxOpenDrafts - 1));
        Assert.True(TripCreateRules.DraftCapReached(TripCreateRules.MaxOpenDrafts));
        Assert.True(TripCreateRules.DraftCapReached(TripCreateRules.MaxOpenDrafts + 5));
    }

    // ---- covers from cached spot sets only

    private sealed class PeekSpots : ITripSpotCache, ITripSpotPeek
    {
        public Dictionary<long, CachedCitySpots> Cached = new();
        public Dictionary<long, CachedCitySpots> Remote = new();
        public TimeSpan RemoteDelay = TimeSpan.Zero;
        public int PeekCalls;
        public int GetCalls;

        public Task<CachedCitySpots?> PeekAsync(long cityKey, CancellationToken ct = default)
        {
            Interlocked.Increment(ref PeekCalls);
            return Task.FromResult(Cached.GetValueOrDefault(cityKey));
        }

        public async Task<CachedCitySpots?> GetAsync(long cityKey, CancellationToken ct = default)
        {
            Interlocked.Increment(ref GetCalls);
            if (RemoteDelay > TimeSpan.Zero) await Task.Delay(RemoteDelay, ct);
            return Cached.GetValueOrDefault(cityKey) ?? Remote.GetValueOrDefault(cityKey);
        }

        public Task InvalidateAsync(long cityKey) => Task.CompletedTask;
    }

    private sealed class Atlas(AtlasCity[] cities, TimeSpan pageDelay) : IAtlasClient
    {
        private static readonly DateTime V = new(2026, 11, 3, 8, 0, 0, DateTimeKind.Utc);
        public bool IsConfigured => true;
        public Task<AtlasCitySpots?> GetCitySpotsAsync(long cityKey, CancellationToken ct = default) => throw new NotSupportedException();

        public async Task<AtlasCityPage> SearchCitiesAsync(string? q, int page, int pageSize, bool readyOnly = false,
            bool popularOnly = false, Guid? stateId = null, CancellationToken ct = default)
        {
            await Task.Delay(pageDelay, ct);
            var items = cities.Skip((page - 1) * pageSize).Take(pageSize).ToList();
            return new AtlasCityPage { Items = items, HasMore = page * pageSize < cities.Length, CatalogUpdatedAt = V, CatalogCount = cities.Length };
        }

        public Task<AtlasCatalogVersion> GetCatalogVersionAsync(CancellationToken ct = default)
            => Task.FromResult(new AtlasCatalogVersion { UpdatedAt = V, Count = cities.Length });
    }

    private sealed class Sp(IAtlasClient atlas, ITripSpotCache spots) : IServiceProvider
    {
        public object? GetService(Type t) => t == typeof(IAtlasClient) ? atlas : t == typeof(ITripSpotCache) ? spots : null;
    }

    private static AtlasCity City(long key) => new()
    {
        CityKey = key, Name = "City" + key.ToString("D4"), DistrictId = Guid.NewGuid(), DistrictName = "D", StateName = "Uttarakhand",
        Latitude = 30, Longitude = 78, Status = "Ready", SpotCount = 5, IsTripEnabled = true, UpdatedAt = DateTime.UtcNow,
    };

    private static CachedCitySpots SpotsWithImage(string url)
        => new() { Spots = [new TripSpotDto { Id = "s", Name = "s", ImageUrl = url, IsFeatured = true }] };

    private static TripCityCatalog CatalogOf(IAtlasClient atlas, ITripSpotCache spots)
        => new(Options.Create(new TripPlannerOptions { CityCatalogRevalidateSeconds = 300, CityCatalogPageSize = 100 }),
            new Sp(atlas, spots), NullLogger<TripCityCatalog>.Instance);

    [Fact]
    public async Task Cold_catalog_reads_cached_spot_sets_only_and_never_waits_on_the_spot_fetch()
    {
        var spots = new PeekSpots { RemoteDelay = TimeSpan.FromSeconds(30) };
        spots.Cached[1] = SpotsWithImage("https://res.cloudinary.com/demo/image/upload/v1/a.jpg");
        var catalog = CatalogOf(new Atlas([City(1), City(2)], TimeSpan.Zero), spots);

        var snapshot = await catalog.GetAsync();

        Assert.NotNull(snapshot!.Cities.Single(c => c.CityKey == 1).CoverUrl);
        Assert.Null(snapshot.Cities.Single(c => c.CityKey == 2).CoverUrl);
        Assert.Equal(2, spots.PeekCalls);
    }

    [Fact]
    public async Task Background_warm_fills_the_cover_of_a_city_whose_spot_set_was_not_cached()
    {
        var spots = new PeekSpots { RemoteDelay = TimeSpan.FromMilliseconds(400) };
        spots.Remote[2] = SpotsWithImage("https://res.cloudinary.com/demo/image/upload/v1/b.jpg");
        var catalog = CatalogOf(new Atlas([City(2)], TimeSpan.Zero), spots);

        var snapshot = await catalog.GetAsync();
        Assert.Null(snapshot!.Cities[0].CoverUrl);

        for (var i = 0; i < 200 && snapshot.Cities[0].CoverUrl == null; i++) await Task.Delay(20);
        Assert.NotNull(snapshot.Cities[0].CoverUrl);
        Assert.Equal(1, spots.GetCalls);
    }

    // Simulated latencies (no Atlas or Redis here): 5 pages of 50 ms and 500 uncached cities at 200 ms per spot set.
    // Before: covers waited on the spot fetch of every city (500 x 200 ms / 8 parallel = about 12.5 s on top of the pages).
    [Fact]
    public async Task Cold_catalog_time_with_simulated_atlas_latency_is_pages_only()
    {
        var cities = Enumerable.Range(1, 500).Select(i => City(i)).ToArray();
        var spots = new PeekSpots { RemoteDelay = TimeSpan.FromMilliseconds(200) };
        var catalog = CatalogOf(new Atlas(cities, TimeSpan.FromMilliseconds(50)), spots);

        var watch = Stopwatch.StartNew();
        var snapshot = await catalog.GetAsync();
        watch.Stop();

        Assert.Equal(500, snapshot!.Cities.Count);
        Assert.True(watch.ElapsedMilliseconds < 2000, $"cold catalog took {watch.ElapsedMilliseconds} ms");
        Console.WriteLine($"B4 cold catalog (simulated): {watch.ElapsedMilliseconds} ms");
    }
}
