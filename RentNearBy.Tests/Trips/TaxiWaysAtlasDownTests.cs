using System.Globalization;
using System.Reflection;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Services;

namespace RentNearBy.Tests.Trips;

// Atlas down must never wipe a good city list, never cache an "empty" answer and never change the key shapes.
public class TaxiWaysAtlasDownTests
{
    private static readonly (double Lat, double Lng) Haldwani = (29.2183, 79.5130);
    private static readonly (double Lat, double Lng) Nainital = (29.3919, 79.4542);
    private static readonly (double Lat, double Lng) PinNearRoad = (29.2183, 79.5500);

    private static readonly TripCityInfo[] Cities =
    [
        new() { CityKey = 210000001, Name = "Haldwani", Latitude = Haldwani.Lat, Longitude = Haldwani.Lng },
        new() { CityKey = 210000003, Name = "Nainital", Latitude = Nainital.Lat, Longitude = Nainital.Lng },
    ];

    private static string Line(params (double Lat, double Lng)[] pts) =>
        "{\"type\":\"LineString\",\"coordinates\":[" + string.Join(",", pts.Select(p =>
            string.Create(CultureInfo.InvariantCulture, $"[{p.Lng},{p.Lat}]"))) + "]}";

    // ---- network: the last good city list survives an Atlas outage ----

    private sealed class Source : ITaxiWaysNetworkSource
    {
        public Task<IReadOnlyList<TaxiWaysSourceRow>> LoadAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TaxiWaysSourceRow>>([TaxiWaysFixture.Row("Haldwani", Haldwani, "Nainital", Nainital, 1500)]);
    }

    private sealed class Catalog : ITripCityCatalog
    {
        public bool Down;
        public Task<TripCityCatalogSnapshot?> GetAsync(CancellationToken ct = default)
        {
            if (Down) throw new HttpRequestException("atlas down");
            return Task.FromResult<TripCityCatalogSnapshot?>(new TripCityCatalogSnapshot { Cities = Cities.ToList() });
        }
        public Task<(bool Available, TripCityInfo? City)> FindAsync(long cityKey, CancellationToken ct = default) => throw new NotSupportedException();
        public Task InvalidateAsync() => Task.CompletedTask;
    }

    private static (TaxiWaysNetwork Network, Catalog Catalog) NewNetwork(bool down)
    {
        var catalog = new Catalog { Down = down };
        var services = new ServiceCollection()
            .AddSingleton<ITaxiWaysNetworkSource>(new Source())
            .AddSingleton<ITripCityCatalog>(catalog)
            .BuildServiceProvider();
        var network = new TaxiWaysNetwork(
            services.GetRequiredService<IServiceScopeFactory>(), services, Options.Create(new TaxiWaysOptions()),
            NullLogger<TaxiWaysNetwork>.Instance);
        return (network, catalog);
    }

    private static async Task<TaxiWaysNetworkSnapshot> RebuiltAsync(TaxiWaysNetwork network, string previousVersion)
    {
        await TaxiWaysVersion.BumpAsync(null);
        for (var i = 0; i < 100; i++)
        {
            var s = await network.GetAsync();
            if (s != null && s.Version != previousVersion) return s;
            await Task.Delay(50);
        }
        throw new TimeoutException("The network did not rebuild");
    }

    [Fact]
    public async Task A_rebuild_while_Atlas_is_down_keeps_the_previous_city_list()
    {
        var (network, catalog) = NewNetwork(down: false);
        var first = (await network.GetAsync())!;
        Assert.NotEmpty(first.CityGrid);
        Assert.False(first.CitiesMissing);

        catalog.Down = true;
        var second = await RebuiltAsync(network, first.Version);

        Assert.NotEmpty(second.CityGrid);
        Assert.False(second.CitiesMissing);
        Assert.Equal(210000001, TaxiWaysSearchEngine.NearestCity(second, Haldwani.Lat, Haldwani.Lng, 30000)?.CityKey);
    }

    [Fact]
    public async Task A_first_build_without_Atlas_has_clusters_only_and_says_so()
    {
        var (network, _) = NewNetwork(down: true);

        var snapshot = (await network.GetAsync())!;

        Assert.Empty(snapshot.CityGrid);
        Assert.True(snapshot.CitiesMissing);
        Assert.NotEmpty(snapshot.Places);
    }

    [Fact]
    public async Task Once_Atlas_is_back_the_clusters_only_snapshot_is_replaced()
    {
        var (network, catalog) = NewNetwork(down: true);
        var first = (await network.GetAsync())!;
        Assert.True(first.CitiesMissing);

        catalog.Down = false;
        var second = await RebuiltAsync(network, first.Version);

        Assert.NotEmpty(second.CityGrid);
        Assert.False(second.CitiesMissing);
    }

    // ---- search: no city list = flag, nothing cached; the pinned key is the key of the result ----

    private sealed class FakeNetwork(TaxiWaysNetworkSnapshot snapshot) : ITaxiWaysNetwork
    {
        public Task<TaxiWaysNetworkSnapshot?> GetAsync(CancellationToken ct = default) => Task.FromResult<TaxiWaysNetworkSnapshot?>(snapshot);
        public string? CurrentVersion => snapshot.Version;
    }

    // Every repository call answers empty: only the engine and the key handling are under test here.
    public class EmptyStub : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Answer(targetMethod!);

        public static object? Answer(MethodInfo targetMethod)
        {
            var type = targetMethod.ReturnType;
            if (type.IsInterface && !type.IsGenericType) return DispatchProxy.Create(type, typeof(EmptyStub));
            if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(Task<>)) throw new NotSupportedException(targetMethod.Name);
            var value = type.GetGenericArguments()[0];
            object? empty = value.IsInterface && value.IsGenericType
                ? Array.CreateInstance(value.GetGenericArguments()[0], 0)
                : value.IsValueType || value.GetConstructor(Type.EmptyTypes) != null ? Activator.CreateInstance(value) : null;
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(value).Invoke(null, [empty]);
        }
    }

    private static TaxiWaysSearch Searcher(TaxiWaysNetworkSnapshot s)
    {
        var uow = DispatchProxy.Create<IUnitOfWork, EmptyStub>();
        var services = new ServiceCollection().AddSingleton(uow).BuildServiceProvider();
        return new TaxiWaysSearch(
            new FakeNetwork(s), services.GetRequiredService<IServiceScopeFactory>(), services,
            new MemoryCache(new MemoryCacheOptions()), Options.Create(new TaxiWaysOptions()), uow,
            NullLogger<TaxiWaysSearch>.Instance);
    }

    // Taxis served in pages exactly like the repository (take / skip), in the order given.
    public class UowStub : DispatchProxy
    {
        public object? Taxis;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod!.Name == "get_Taxis" ? Taxis : EmptyStub.Answer(targetMethod);
    }

    public class TaxiPages : DispatchProxy
    {
        public List<(TaxiBrowseDto Item, double Lat, double Lng)> All = [];
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name != nameof(ITaxiListingRepository.BrowseLocalNearAsync)) return EmptyStub.Answer(targetMethod);
            var take = (int)args![4]!; var skip = (int)args[5]!;
            return Task.FromResult<IReadOnlyList<(TaxiBrowseDto Item, double Lat, double Lng)>>(All.Skip(skip).Take(take).ToList());
        }
    }

    [Fact]
    public async Task Local_taxis_of_the_destination_city_are_found_beyond_the_first_500_of_the_box()
    {
        var bhimtal = new TripCityInfo { CityKey = 210000005, Name = "Bhimtal", Latitude = 29.3436, Longitude = 79.5535 };
        var s = Build([.. Cities, bhimtal]);
        var pages = DispatchProxy.Create<ITaxiListingRepository, TaxiPages>();
        var all = ((TaxiPages)(object)pages).All;
        for (var i = 0; i < 700; i++) all.Add((new TaxiBrowseDto { Id = Guid.NewGuid() }, bhimtal.Latitude, bhimtal.Longitude));
        for (var i = 0; i < 20; i++) all.Add((new TaxiBrowseDto { Id = Guid.NewGuid() }, Nainital.Lat, Nainital.Lng));
        var uow = DispatchProxy.Create<IUnitOfWork, UowStub>();
        ((UowStub)(object)uow).Taxis = pages;
        var services = new ServiceCollection().AddSingleton(uow).BuildServiceProvider();
        var search = new TaxiWaysSearch(
            new FakeNetwork(s), services.GetRequiredService<IServiceScopeFactory>(), services,
            new MemoryCache(new MemoryCacheOptions()), Options.Create(new TaxiWaysOptions()), uow, NullLogger<TaxiWaysSearch>.Instance);

        var result = await search.SearchAsync(new TaxiWaysQuery(Haldwani.Lat, Haldwani.Lng, Nainital.Lat, Nainital.Lng, 4));

        Assert.Equal(Math.Min(20, new TaxiWaysOptions().MaxLocalTaxis), result!.LocalTaxis.Count);
    }

    private static TaxiWaysSourceRow Road() =>
        TaxiWaysFixture.Row("Road start", (29.0700, 79.5600), "Nainital", Nainital, 1900, taxi: Guid.NewGuid());

    private static TaxiWaysNetworkSnapshot Build(IReadOnlyList<TripCityInfo> cities, string version = "t")
    {
        var row = Road();
        row.GeometryJson = Line((29.0700, 79.5600), (29.2183, 79.5600), Nainital);
        return TaxiWaysClustering.Build([row], cities, TaxiWaysFixture.Options, version, DateTime.UtcNow);
    }

    [Fact]
    public async Task Without_a_city_list_the_search_flags_it_and_the_next_search_with_cities_is_not_stale()
    {
        var search = Searcher(Build([]));
        var down = await search.SearchAsync(new TaxiWaysQuery(Haldwani.Lat, Haldwani.Lng, Nainital.Lat, Nainital.Lng, 4));
        Assert.NotNull(down);
        Assert.True(down!.CitiesUnavailable);
        Assert.Empty(down.Stays);
        Assert.Empty(down.LocalTaxis);

        // Same version string, Atlas back: the answer must come from the new snapshot, not the cluster-only cache.
        var up = Searcher(Build(Cities));
        var good = await up.SearchAsync(new TaxiWaysQuery(Haldwani.Lat, Haldwani.Lng, Nainital.Lat, Nainital.Lng, 4));
        Assert.False(good!.CitiesUnavailable);
    }

    [Fact]
    public async Task The_result_key_of_a_pin_is_the_pinned_ref_and_leg_and_plan_queries_keep_the_driver()
    {
        var s = Build(Cities);
        var search = Searcher(s);

        var result = await search.SearchAsync(new TaxiWaysQuery(PinNearRoad.Lat, PinNearRoad.Lng, Nainital.Lat, Nainital.Lng, 4));

        Assert.NotNull(result);
        Assert.StartsWith(TaxiWaysKeys.PlaceKeyAtlas(210000001) + ":p", result!.From.Key);
        Assert.StartsWith(TaxiWaysKeys.PlaceKeyAtlas(210000003), result.To.Key);
        var driver = Assert.Single(result.AllTheWay);

        var legKey = TaxiWaysKeys.LegKey(result.From.Key, result.To.Key);
        var page = await search.LegDriversAsync(legKey, new LegDriverQuery(4));
        Assert.Equal(driver.TaxiId, Assert.Single(page!.Items).TaxiId);

        var plan = await search.PlanAsync(TaxiWaysKeys.PlanKey([result.From.Key, result.To.Key]), 4);
        Assert.Equal(driver.TaxiId, Assert.Single(Assert.Single(plan!.Legs).Drivers!).TaxiId);
    }

    [Fact]
    public async Task A_plan_leg_lists_the_cities_on_the_road_without_the_ends()
    {
        var places = new[]
        {
            new TripCityInfo { CityKey = 210000001, Name = "Haldwani", Latitude = Haldwani.Lat, Longitude = Haldwani.Lng },
            new TripCityInfo { CityKey = 210000002, Name = "Kathgodam", Latitude = 29.2656, Longitude = 79.5262 },
            new TripCityInfo { CityKey = 210000003, Name = "Nainital", Latitude = Nainital.Lat, Longitude = Nainital.Lng },
        };
        var row = TaxiWaysFixture.Row("Haldwani", Haldwani, "Nainital", Nainital, 1500);
        row.GeometryJson = Line(Haldwani, (29.2656, 79.5262), Nainital);
        var s = TaxiWaysClustering.Build([row], places, TaxiWaysFixture.Options, "t", DateTime.UtcNow);
        var a = TaxiWaysSearchEngine.Resolve(s, Haldwani.Lat, Haldwani.Lng, false, TaxiWaysFixture.Options);
        var b = TaxiWaysSearchEngine.Resolve(s, Nainital.Lat, Nainital.Lng, false, TaxiWaysFixture.Options);
        var plan = TaxiWaysSearchEngine.BuildPlan(s, [a, b], 4, TaxiWaysFixture.Options)!;

        var detail = await Searcher(s).PlanAsync(plan.PlanKey, 4);

        var leg = Assert.Single(detail!.Legs);
        var stop = Assert.Single(leg.PassStops);
        Assert.Equal("Kathgodam", stop.Name);
        Assert.Equal(["Kathgodam"], leg.Passes);
    }

    [Fact]
    public void A_route_start_8_km_from_its_nearest_city_keeps_its_own_place()
    {
        var far = (29.2183 - 0.072, 79.5130);
        var row = TaxiWaysFixture.Row("Outskirts", far, "Nainital", Nainital, 1500);
        var s = TaxiWaysClustering.Build([row], Cities, TaxiWaysFixture.Options, "t", DateTime.UtcNow);

        var start = s.Places[s.Routes[row.RouteId].FromPlace];
        Assert.Null(start.CityKey);
        Assert.Equal("Outskirts", start.Name);
    }

    [Fact]
    public void A_route_start_3_km_from_its_nearest_city_is_attached_to_that_city()
    {
        var near = (29.2183 - 0.027, 79.5130);
        var row = TaxiWaysFixture.Row("Outskirts", near, "Nainital", Nainital, 1500);
        var s = TaxiWaysClustering.Build([row], Cities, TaxiWaysFixture.Options, "t", DateTime.UtcNow);

        Assert.Equal(210000001, s.Places[s.Routes[row.RouteId].FromPlace].CityKey);
    }

    [Fact]
    public async Task A_catalog_invalidate_changes_the_network_version()
    {
        var (network, _) = NewNetwork(down: false);
        var first = (await network.GetAsync())!;
        var services = new ServiceCollection().BuildServiceProvider();
        var real = new TripCityCatalog(
            Options.Create(new TripPlannerOptions()), services, NullLogger<TripCityCatalog>.Instance);

        await real.InvalidateAsync();

        var second = await RebuiltAsyncNoBump(network, first.Version);
        Assert.NotEqual(first.Version, second.Version);
    }

    private static async Task<TaxiWaysNetworkSnapshot> RebuiltAsyncNoBump(TaxiWaysNetwork network, string previousVersion)
    {
        for (var i = 0; i < 100; i++)
        {
            var s = await network.GetAsync();
            if (s != null && s.Version != previousVersion) return s;
            await Task.Delay(50);
        }
        throw new TimeoutException("The network did not rebuild");
    }
}
