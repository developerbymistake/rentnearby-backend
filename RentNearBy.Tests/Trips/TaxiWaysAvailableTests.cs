using System.Globalization;
using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RentNearBy.Api.Extensions;
using RentNearBy.Api.Handlers;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Infrastructure.Services;

namespace RentNearBy.Tests.Trips;

// GET /taxis/ways/available: true only when GET /taxis/ways would show a driver all the way or a plan; local taxis are not a way.
public class TaxiWaysAvailableTests
{
    private static readonly (double Lat, double Lng) Haldwani = (29.2183, 79.5130);
    private static readonly (double Lat, double Lng) Nainital = (29.3919, 79.4542);

    private static readonly TripCityInfo[] Cities =
    [
        new() { CityKey = 210000001, Name = "Haldwani", Latitude = Haldwani.Lat, Longitude = Haldwani.Lng },
        new() { CityKey = 210000003, Name = "Nainital", Latitude = Nainital.Lat, Longitude = Nainital.Lng },
    ];

    // ---- what counts as a way (pure) ----

    [Fact]
    public void No_result_is_no_way() => Assert.False(TaxiWaysAvailability.HasWay(null));

    [Fact]
    public void A_driver_all_the_way_is_a_way() =>
        Assert.True(TaxiWaysAvailability.HasWay(new TaxiWaysResultDto { AllTheWay = [new WayDriverDto()] }));

    [Fact]
    public void A_plan_of_cars_is_a_way() =>
        Assert.True(TaxiWaysAvailability.HasWay(new TaxiWaysResultDto { Plans = [new WayPlanDto { Cars = 2 }] }));

    [Fact]
    public void Local_taxis_stays_and_a_reach_first_place_alone_are_not_a_way() =>
        Assert.False(TaxiWaysAvailability.HasWay(new TaxiWaysResultDto
        {
            LocalTaxis = [new TaxiBrowseDto { Id = Guid.NewGuid() }],
            Stays = [new StayNearbyDto()],
            ReachFirstPlace = new WayPlaceDto { Key = "a210000002", Name = "Kathgodam" },
        }));

    // ---- cache key: one per from/to cell, version and people ----

    [Fact]
    public void Two_pins_in_the_same_cell_share_the_key()
    {
        var a = TaxiWaysKeys.Available("t~w3", 29.21831, 79.51301, 29.39191, 79.45421, 2, false);
        var b = TaxiWaysKeys.Available("t~w3", 29.21834, 79.51298, 29.39188, 79.45419, 2, false);
        Assert.Equal(a, b);
        Assert.StartsWith("taxi:ways:avail:t~w3:", a);
    }

    [Fact]
    public void Version_people_cell_and_my_location_each_change_the_key()
    {
        var k = TaxiWaysKeys.Available("t1", Haldwani.Lat, Haldwani.Lng, Nainital.Lat, Nainital.Lng, 2, false);
        Assert.NotEqual(k, TaxiWaysKeys.Available("t2", Haldwani.Lat, Haldwani.Lng, Nainital.Lat, Nainital.Lng, 2, false));
        Assert.NotEqual(k, TaxiWaysKeys.Available("t1", Haldwani.Lat, Haldwani.Lng, Nainital.Lat, Nainital.Lng, 6, false));
        Assert.NotEqual(k, TaxiWaysKeys.Available("t1", Haldwani.Lat, Haldwani.Lng, Nainital.Lat + 0.01, Nainital.Lng, 2, false));
        Assert.NotEqual(k, TaxiWaysKeys.Available("t1", Haldwani.Lat, Haldwani.Lng, Nainital.Lat, Nainital.Lng, 2, true));
    }

    // ---- search: same plan list as the feed, no DB, cached ----

    private sealed class SwapNetwork(TaxiWaysNetworkSnapshot snapshot) : ITaxiWaysNetwork
    {
        public TaxiWaysNetworkSnapshot? Snapshot = snapshot;
        public Task<TaxiWaysNetworkSnapshot?> GetAsync(CancellationToken ct = default) => Task.FromResult(Snapshot);
        public string? CurrentVersion => Snapshot?.Version;
    }

    // Any repository call fails: the check must never reach the database.
    public class NoDb : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new InvalidOperationException("DB touched: " + targetMethod?.Name);
    }

    private static TaxiWaysSearch Searcher(ITaxiWaysNetwork network, IMemoryCache memory, IUnitOfWork? uow = null)
    {
        uow ??= DispatchProxy.Create<IUnitOfWork, NoDb>();
        var services = new ServiceCollection().AddSingleton(uow).BuildServiceProvider();
        return new TaxiWaysSearch(
            network, services.GetRequiredService<IServiceScopeFactory>(), services, memory,
            Options.Create(new TaxiWaysOptions()), uow, NullLogger<TaxiWaysSearch>.Instance);
    }

    // The plan cache's single flight is process-wide: a version of its own keeps parallel test classes off each other's keys.
    private static string Ver() => "avail" + Guid.NewGuid().ToString("N");

    private static TaxiWaysNetworkSnapshot Direct(string version) =>
        TaxiWaysClustering.Build([TaxiWaysFixture.Row("Haldwani", Haldwani, "Nainital", Nainital, 1500)], Cities, TaxiWaysFixture.Options, version, DateTime.UtcNow);

    // A live network whose only route is far away: nothing reaches Nainital from Haldwani.
    private static TaxiWaysNetworkSnapshot Elsewhere(string version) =>
        TaxiWaysClustering.Build(
            [TaxiWaysFixture.Row("Delhi", TaxiWaysFixture.Delhi, "Lucknow", TaxiWaysFixture.Lucknow, 5000)],
            Cities, TaxiWaysFixture.Options, version, DateTime.UtcNow);

    private static TaxiWaysQuery Query(double fromLat = 29.2183, double fromLng = 79.5130) =>
        new(fromLat, fromLng, Nainital.Lat, Nainital.Lng, 2);

    [Fact]
    public async Task A_driver_to_the_destination_answers_true_without_touching_the_database()
    {
        var result = await Searcher(new SwapNetwork(Direct(Ver())), new MemoryCache(new MemoryCacheOptions())).AvailableAsync(Query());

        Assert.True(result!.Available);
    }

    [Fact]
    public async Task No_route_to_the_destination_answers_false()
    {
        var result = await Searcher(new SwapNetwork(Elsewhere(Ver())), new MemoryCache(new MemoryCacheOptions())).AvailableAsync(Query());

        Assert.False(result!.Available);
    }

    [Fact]
    public async Task Local_taxis_in_the_destination_city_are_not_a_way()
    {
        var pages = DispatchProxy.Create<ITaxiListingRepository, TaxiWaysAtlasDownTests.TaxiPages>();
        ((TaxiWaysAtlasDownTests.TaxiPages)(object)pages).All.Add((new TaxiBrowseDto { Id = Guid.NewGuid() }, Nainital.Lat, Nainital.Lng));
        var uow = DispatchProxy.Create<IUnitOfWork, TaxiWaysAtlasDownTests.UowStub>();
        ((TaxiWaysAtlasDownTests.UowStub)(object)uow).Taxis = pages;
        var network = new SwapNetwork(Elsewhere(Ver()));

        var feed = await Searcher(network, new MemoryCache(new MemoryCacheOptions()), uow).SearchAsync(Query());
        var check = await Searcher(network, new MemoryCache(new MemoryCacheOptions())).AvailableAsync(Query());

        Assert.NotEmpty(feed!.LocalTaxis);
        Assert.Empty(feed.AllTheWay);
        Assert.Empty(feed.Plans);
        Assert.False(check!.Available);
    }

    [Fact]
    public async Task The_answer_matches_the_feed_for_the_same_input()
    {
        var network = new SwapNetwork(Direct(Ver()));
        var memory = new MemoryCache(new MemoryCacheOptions());
        var uow = DispatchProxy.Create<IUnitOfWork, TaxiWaysAtlasDownTests.EmptyStub>();

        var feed = await Searcher(network, new MemoryCache(new MemoryCacheOptions()), uow).SearchAsync(Query());
        var check = await Searcher(network, memory).AvailableAsync(Query());

        Assert.NotEmpty(feed!.AllTheWay);
        Assert.True(check!.Available);
    }

    [Fact]
    public async Task No_network_answers_null()
    {
        var network = new SwapNetwork(Direct(Ver())) { Snapshot = null };

        Assert.Null(await Searcher(network, new MemoryCache(new MemoryCacheOptions())).AvailableAsync(Query()));
    }

    [Fact]
    public async Task A_repeat_in_the_same_cell_is_served_from_the_cache_until_the_network_version_moves()
    {
        var v1 = Ver();
        var network = new SwapNetwork(Direct(v1));
        var memory = new MemoryCache(new MemoryCacheOptions());
        var search = Searcher(network, memory);

        Assert.True((await search.AvailableAsync(Query()))!.Available);
        var entries = memory.Count;

        // Same version, routes gone underneath: the cached answer stands and no new entry is made for a pin in the same cell.
        network.Snapshot = Elsewhere(v1);
        Assert.True((await search.AvailableAsync(Query(29.21834, 79.51298)))!.Available);
        Assert.Equal(entries, memory.Count);

        // A rebuild (new version) is a new key: the answer is computed again from the new network.
        network.Snapshot = Elsewhere(Ver());
        Assert.False((await search.AvailableAsync(Query()))!.Available);
    }

    // ---- handler: same validation, auth and rate limit style as GET /taxis/ways ----

    private sealed class FakeSearch : ITaxiWaysSearch
    {
        public TaxiWaysQuery? Seen;
        public TaxiWaysAvailabilityDto? Result = new() { Available = true };

        public Task<TaxiWaysAvailabilityDto?> AvailableAsync(TaxiWaysQuery q, CancellationToken ct = default)
        {
            Seen = q;
            return Task.FromResult(Result);
        }

        public Task<TaxiWaysResultDto?> SearchAsync(TaxiWaysQuery q, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WayPlanDetailDto?> PlanAsync(string planKey, int people, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WayDriverPageDto?> LegDriversAsync(string legKey, LegDriverQuery q, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Dictionary<Guid, LegTaxiFare>> LegFaresAsync(
            string legKey, IReadOnlyCollection<Guid> taxiIds, Guid? excludeRoute = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class Limiter(bool allowed) : IRateLimitService
    {
        public readonly List<string> Keys = [];
        public Task<RateLimitResult> CheckAsync(string key, int maxAttempts, TimeSpan window, int permits = 1)
        {
            Keys.Add(key);
            return Task.FromResult(new RateLimitResult(allowed, maxAttempts, allowed ? null : TimeSpan.FromSeconds(20)));
        }
        public Task ReleaseAsync(string key, string token) => Task.CompletedTask;
    }

    private static readonly Guid User = Guid.NewGuid();
    private static string Today => TaxiIdentityRules.TodayIst().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static ClaimsPrincipal Caller(Guid? id) =>
        new(new ClaimsIdentity(id == null ? [] : [new Claim(ClaimTypes.NameIdentifier, id.Value.ToString())], "test"));

    private static async Task<(IResult Result, int Status)> CallAsync(
        FakeSearch search, double? fromLat = 29.2183, double? toLat = 29.3919, string? date = "", int? people = 2,
        Limiter? limiter = null, Guid? caller = null, bool anonymous = false, HttpContext? http = null)
    {
        var result = await TaxiWaysHandlers.GetWaysAvailable(
            fromLat, 79.5130, toLat, 79.4542, date == "" ? Today : date, people, null, null,
            search, limiter ?? new Limiter(true), Caller(anonymous ? null : caller ?? User), http ?? new DefaultHttpContext(),
            Options.Create(new TaxiWaysOptions()), CancellationToken.None);
        return (result, ((IStatusCodeHttpResult)result).StatusCode ?? 200);
    }

    [Fact]
    public async Task A_valid_query_answers_the_bool_in_the_ok_envelope()
    {
        var search = new FakeSearch { Result = new TaxiWaysAvailabilityDto { Available = false } };

        var (result, status) = await CallAsync(search);

        Assert.Equal(200, status);
        var body = Assert.IsType<ApiResponse<TaxiWaysAvailabilityDto>>(((IValueHttpResult)result).Value);
        Assert.False(body.Data!.Available);
        Assert.Equal(2, search.Seen!.People);
        Assert.Equal(Nainital.Lat, search.Seen.ToLat);
    }

    [Fact]
    public async Task People_defaults_like_the_ways_feed()
    {
        var search = new FakeSearch();

        await CallAsync(search, people: null);

        Assert.Equal(new TaxiWaysOptions().DefaultPeople, search.Seen!.People);
    }

    [Theory]
    [InlineData(91.0, 29.39)]
    [InlineData(29.2, -91.0)]
    public async Task Bad_coordinates_are_a_bad_request(double fromLat, double toLat)
    {
        var search = new FakeSearch();

        var (_, status) = await CallAsync(search, fromLat, toLat);

        Assert.Equal(400, status);
        Assert.Null(search.Seen);
    }

    [Fact]
    public async Task Missing_coordinates_are_a_bad_request()
    {
        var search = new FakeSearch();

        var (_, status) = await CallAsync(search, fromLat: null);

        Assert.Equal(400, status);
        Assert.Null(search.Seen);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("2020-01-01")]
    [InlineData("01-12-2026")]
    public async Task A_missing_past_or_badly_shaped_date_is_a_bad_request(string? date)
    {
        var search = new FakeSearch();

        var (result, status) = await CallAsync(search, date: date);

        Assert.Equal(400, status);
        Assert.Equal(TaxiWaysErrorTypes.InvalidDate, ((ApiResponse<object>)((IValueHttpResult)result).Value!).Error!.Type);
        Assert.Null(search.Seen);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(13)]
    public async Task People_out_of_range_is_a_bad_request(int people)
    {
        var search = new FakeSearch();

        var (_, status) = await CallAsync(search, people: people);

        Assert.Equal(400, status);
        Assert.Null(search.Seen);
    }

    [Fact]
    public async Task No_network_answers_503()
    {
        var (result, status) = await CallAsync(new FakeSearch { Result = null });

        Assert.Equal(503, status);
        Assert.Equal(TaxiWaysErrorTypes.TaxiWaysUnavailable, ((ApiResponse<object>)((IValueHttpResult)result).Value!).Error!.Type);
    }

    [Fact]
    public async Task A_caller_without_a_user_id_is_unauthorized()
    {
        var search = new FakeSearch();

        var (_, status) = await CallAsync(search, anonymous: true);

        Assert.Equal(401, status);
        Assert.Null(search.Seen);
    }

    [Fact]
    public async Task The_check_has_its_own_per_user_bucket()
    {
        var limiter = new Limiter(true);

        await CallAsync(new FakeSearch(), limiter: limiter);

        Assert.Equal($"taxi:ways:available:{User}", Assert.Single(limiter.Keys));
    }

    [Fact]
    public async Task Over_the_limit_answers_429_with_retry_after_and_never_searches()
    {
        var search = new FakeSearch();
        var http = new DefaultHttpContext();

        var (_, status) = await CallAsync(search, limiter: new Limiter(false), http: http);

        Assert.Equal(429, status);
        Assert.Equal("20", http.Response.Headers["Retry-After"].ToString());
        Assert.Null(search.Seen);
    }
}
