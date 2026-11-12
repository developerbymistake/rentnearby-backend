using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RentNearBy.Api.Handlers;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Services;

namespace RentNearBy.Tests.Trips;

internal static class CityFixture
{
    public static readonly TripCityInfo Haldwani = new() { CityKey = 210000001, Name = "Haldwani", Latitude = 29.2183, Longitude = 79.5130 };
    public static readonly TripCityInfo Kathgodam = new() { CityKey = 210000002, Name = "Kathgodam", Latitude = 29.2656, Longitude = 79.5262 };
    public static readonly TripCityInfo Nainital = new() { CityKey = 210000003, Name = "Nainital", Latitude = 29.3919, Longitude = 79.4542 };
    public static readonly (double Lat, double Lng) NearHaldwani = (29.2200, 79.5140);
    public static readonly (double Lat, double Lng) NearKathgodam = (29.2650, 79.5260);
    public static readonly (double Lat, double Lng) Nowhere = (22.0, 75.0);

    public sealed class Net(TaxiWaysNetworkSnapshot? snapshot) : ITaxiWaysNetwork
    {
        public Task<TaxiWaysNetworkSnapshot?> GetAsync(CancellationToken ct = default) => Task.FromResult(snapshot);
        public string? CurrentVersion => "t";
    }

    public static CityResolver Resolver(params TripCityInfo[] cities)
        => new(new Net(TaxiWaysClustering.Build([], cities, new TaxiWaysOptions(), "t", DateTime.UtcNow)), Options.Create(new TaxiWaysOptions()));

    public static CityResolver NoSnapshot() => new(new Net(null), Options.Create(new TaxiWaysOptions()));

    public static StayNearbyDto Stay((double Lat, double Lng) at, short rank = 0) => new()
    {
        Id = Guid.NewGuid(), Latitude = (decimal)at.Lat, Longitude = (decimal)at.Lng, PriorityRank = rank, IsActive = true,
    };

    public static TaxiAt Taxi((double Lat, double Lng) at, short rank = 0, int? rate = 1800, short seats = 6, string category = "Sedan") => new(
        new TaxiBrowseDto { Id = Guid.NewGuid(), LocalDayRate = rate, LocalDayKm = 80, Seats = seats, PriorityRank = rank, VehicleCategory = category },
        at.Lat, at.Lng);

    public static ClaimsPrincipal Caller() =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], "test"));

    public sealed class AllowAll : IRateLimitService
    {
        public Task<RateLimitResult> CheckAsync(string key, int maxAttempts, TimeSpan window, int permits = 1) =>
            Task.FromResult(new RateLimitResult(true, maxAttempts, null));
        public Task ReleaseAsync(string key, string token) => Task.CompletedTask;
    }

    public class StayRepoStub : DispatchProxy
    {
        public List<StayNearbyDto> All = [];
        public Guid? SeenDistrict = Guid.NewGuid();
        public int Calls;

        protected override object? Invoke(MethodInfo? m, object?[]? a)
        {
            if (m?.Name != nameof(IStayListingRepository.GetNearbyAsync)) throw new NotSupportedException(m?.Name);
            Calls++;
            SeenDistrict = (Guid?)a![3];
            var take = (int)a[5]; var skip = (int)a[6];
            var rows = All.Skip(skip);
            if (take > 0) rows = rows.Take(take);
            return Task.FromResult<IEnumerable<StayNearbyDto>>(rows.ToList());
        }
    }

    public class TaxiRepoStub : DispatchProxy
    {
        public List<TaxiAt> All = [];
        public List<(int Take, int Skip)> Pages = [];
        public int Calls;

        protected override object? Invoke(MethodInfo? m, object?[]? a)
        {
            if (m?.Name != nameof(ITaxiListingRepository.BrowseLocalNearAsync)) throw new NotSupportedException(m?.Name);
            Calls++;
            var rate = (bool)a![3]; var take = (int)a[4]; var skip = (int)a[5];
            Pages.Add((take, skip));
            var rows = All.Where(t => !rate || t.Item.LocalDayRate != null).Skip(skip).Take(take).Select(t => (t.Item, t.Lat, t.Lng)).ToList();
            return Task.FromResult<IReadOnlyList<(TaxiBrowseDto Item, double Lat, double Lng)>>(rows);
        }
    }

    public class UowStub : DispatchProxy
    {
        public IStayListingRepository? Stays;
        public ITaxiListingRepository? Taxis;

        protected override object? Invoke(MethodInfo? m, object?[]? a) => m?.Name switch
        {
            "get_Stays" => Stays,
            "get_Taxis" => Taxis,
            _ => throw new NotSupportedException(m?.Name),
        };
    }

    public static (IUnitOfWork Uow, StayRepoStub Stays, TaxiRepoStub Taxis) Uow()
    {
        var stays = DispatchProxy.Create<IStayListingRepository, StayRepoStub>();
        var taxis = DispatchProxy.Create<ITaxiListingRepository, TaxiRepoStub>();
        var uow = DispatchProxy.Create<IUnitOfWork, UowStub>();
        ((UowStub)(object)uow).Stays = stays;
        ((UowStub)(object)uow).Taxis = taxis;
        return (uow, (StayRepoStub)(object)stays, (TaxiRepoStub)(object)taxis);
    }

    public static IServiceProvider NoRedis() => new ServiceCollection().BuildServiceProvider();

    public static (int Status, JsonElement Data, string? ErrorType) Read(IResult result)
    {
        var status = ((Microsoft.AspNetCore.Http.IStatusCodeHttpResult)result).StatusCode ?? 200;
        var value = result.GetType().GetProperty("Value")!.GetValue(result);
        var root = JsonSerializer.SerializeToElement(value);
        var data = root.TryGetProperty("Data", out var d) ? d : default;
        string? type = root.TryGetProperty("Error", out var e) && e.ValueKind == JsonValueKind.Object ? e.GetProperty("Type").GetString() : null;
        return (status, data, type);
    }

    public static MemoryCache TabsCache()
    {
        var memory = new MemoryCache(new MemoryCacheOptions());
        memory.Set(AppTabCache.Key, new List<AppTabDto>());
        return memory;
    }
}

public class CityScopedSupplyTests
{
    [Fact]
    public async Task Pin_resolves_to_exactly_one_city_never_the_neighbour()
    {
        var r = CityFixture.Resolver(CityFixture.Haldwani, CityFixture.Kathgodam, CityFixture.Nainital);

        var h = await r.ResolveAsync(CityFixture.NearHaldwani.Lat, CityFixture.NearHaldwani.Lng);
        var k = await r.ResolveAsync(CityFixture.NearKathgodam.Lat, CityFixture.NearKathgodam.Lng);

        Assert.Equal(CityFixture.Haldwani.CityKey, h.City!.CityKey);
        Assert.Equal(CityFixture.Kathgodam.CityKey, k.City!.CityKey);
    }

    [Fact]
    public async Task Pin_beyond_the_guard_belongs_to_no_city_but_cities_are_available()
    {
        var r = CityFixture.Resolver(CityFixture.Haldwani);

        var pin = await r.ResolveAsync(CityFixture.Nowhere.Lat, CityFixture.Nowhere.Lng);

        Assert.True(pin.Available);
        Assert.Null(pin.City);
    }

    [Fact]
    public async Task No_snapshot_or_empty_city_grid_is_unavailable_never_a_guess()
    {
        var down = CityFixture.NoSnapshot();
        var empty = CityFixture.Resolver();

        Assert.False((await down.ResolveAsync(29.22, 79.51)).Available);
        Assert.False((await down.FindAsync(CityFixture.Haldwani.CityKey)).Available);
        Assert.False((await empty.ResolveAsync(29.22, 79.51)).Available);
        Assert.Empty(await empty.FilterAsync([CityFixture.Stay(CityFixture.NearHaldwani)], s => ((double)s.Latitude, (double)s.Longitude), CityFixture.Haldwani.CityKey));
    }

    [Fact]
    public async Task Unknown_city_key_is_available_but_has_no_city()
    {
        var pin = await CityFixture.Resolver(CityFixture.Haldwani).FindAsync(999);

        Assert.True(pin.Available);
        Assert.Null(pin.City);
    }

    [Fact]
    public async Task Filter_keeps_only_items_whose_own_nearest_city_is_the_city()
    {
        var r = CityFixture.Resolver(CityFixture.Haldwani, CityFixture.Kathgodam);
        var haldwaniStay = CityFixture.Stay(CityFixture.NearHaldwani);
        var kathgodamStay = CityFixture.Stay(CityFixture.NearKathgodam);
        var all = new[] { haldwaniStay, kathgodamStay };

        var inHaldwani = await r.FilterAsync(all, s => ((double)s.Latitude, (double)s.Longitude), CityFixture.Haldwani.CityKey);
        var inKathgodam = await r.FilterAsync(all, s => ((double)s.Latitude, (double)s.Longitude), CityFixture.Kathgodam.CityKey);

        Assert.Equal([haldwaniStay.Id], inHaldwani.Select(s => s.Id));
        Assert.Equal([kathgodamStay.Id], inKathgodam.Select(s => s.Id));
    }

    [Fact]
    public async Task More_than_500_taxis_in_the_box_are_all_considered()
    {
        var (uow, _, stub) = CityFixture.Uow();
        stub.All = Enumerable.Range(0, 600).Select(_ => CityFixture.Taxi(CityFixture.NearKathgodam, rank: 5))
            .Concat(Enumerable.Range(0, 100).Select(_ => CityFixture.Taxi(CityFixture.NearHaldwani))).ToList();
        var r = CityFixture.Resolver(CityFixture.Haldwani, CityFixture.Kathgodam);

        var fetch = await CityListings.TaxisAsync(uow.Taxis, r, CityFixture.Haldwani, true);

        Assert.Equal(100, fetch.Items.Count);
        Assert.False(fetch.Capped);
        Assert.Equal([(500, 0), (500, 500)], stub.Pages);
    }

    [Fact]
    public async Task Fetch_is_capped_at_5000_and_says_so()
    {
        var calls = new List<(int Take, int Skip)>();

        var fetch = await CityListings.CollectAsync<int>(
            (take, skip) => { calls.Add((take, skip)); return Task.FromResult<IReadOnlyList<int>>(Enumerable.Range(skip, take).ToList()); },
            rows => Task.FromResult(rows));

        Assert.True(fetch.Capped);
        Assert.Equal(5000, fetch.Items.Count);
        Assert.Equal(10, calls.Count);
    }

    [Fact]
    public async Task A_short_page_ends_the_fetch_without_the_cap_flag()
    {
        var fetch = await CityListings.CollectAsync<int>(
            (take, skip) => Task.FromResult<IReadOnlyList<int>>(Enumerable.Range(0, 120).ToList()), rows => Task.FromResult(rows));

        Assert.False(fetch.Capped);
        Assert.Equal(120, fetch.Items.Count);
    }

    private static async Task<IResult> Supply(IUnitOfWork uow, ICityResolver resolver, IMemoryCache memory, long cityKey) =>
        await TripHandlers.GetCitySupply(cityKey, CityFixture.Caller(), resolver, uow, new CityFixture.AllowAll(), memory,
            CityFixture.NoRedis(), new DefaultHttpContext());

    [Fact]
    public async Task Supply_counts_equal_the_city_lists_and_ignore_the_neighbour_city()
    {
        var (uow, stays, taxis) = CityFixture.Uow();
        stays.All = [CityFixture.Stay(CityFixture.NearHaldwani), CityFixture.Stay(CityFixture.NearHaldwani), CityFixture.Stay(CityFixture.NearKathgodam)];
        taxis.All = [CityFixture.Taxi(CityFixture.NearHaldwani), CityFixture.Taxi(CityFixture.NearKathgodam), CityFixture.Taxi(CityFixture.NearKathgodam)];
        var r = CityFixture.Resolver(CityFixture.Haldwani, CityFixture.Kathgodam);

        var h = CityFixture.Read(await Supply(uow, r, CityFixture.TabsCache(), CityFixture.Haldwani.CityKey));
        var k = CityFixture.Read(await Supply(uow, r, CityFixture.TabsCache(), CityFixture.Kathgodam.CityKey));

        Assert.Equal((2, 1), (h.Data.GetProperty("Stays").GetInt32(), h.Data.GetProperty("Drivers").GetInt32()));
        Assert.Equal((1, 2), (k.Data.GetProperty("Stays").GetInt32(), k.Data.GetProperty("Drivers").GetInt32()));
        Assert.Equal(JsonValueKind.Null, h.Data.GetProperty("DistrictId").ValueKind);
    }

    [Fact]
    public async Task Supply_is_not_stale_after_a_stay_or_taxi_change()
    {
        var (uow, stays, _) = CityFixture.Uow();
        stays.All = [CityFixture.Stay(CityFixture.NearHaldwani)];
        var r = CityFixture.Resolver(CityFixture.Haldwani);
        var memory = CityFixture.TabsCache();

        var first = CityFixture.Read(await Supply(uow, r, memory, CityFixture.Haldwani.CityKey));
        stays.All = [CityFixture.Stay(CityFixture.NearHaldwani), CityFixture.Stay(CityFixture.NearHaldwani)];
        var cached = CityFixture.Read(await Supply(uow, r, memory, CityFixture.Haldwani.CityKey));
        await TripSupplyCache.BumpAsync(null, Guid.NewGuid());
        var fresh = CityFixture.Read(await Supply(uow, r, memory, CityFixture.Haldwani.CityKey));

        Assert.Equal(1, first.Data.GetProperty("Stays").GetInt32());
        Assert.Equal(1, cached.Data.GetProperty("Stays").GetInt32());
        Assert.Equal(2, fresh.Data.GetProperty("Stays").GetInt32());
    }

    [Fact]
    public async Task Atlas_down_gives_503_and_caches_nothing()
    {
        var (uow, stays, _) = CityFixture.Uow();
        var memory = CityFixture.TabsCache();

        var result = CityFixture.Read(await Supply(uow, CityFixture.NoSnapshot(), memory, CityFixture.Haldwani.CityKey));

        Assert.Equal(503, result.Status);
        Assert.Equal(TripErrorTypes.CitiesUnavailable, result.ErrorType);
        Assert.Equal(0, stays.Calls);
        Assert.Equal(1, memory.Count);
    }

    [Fact]
    public async Task Supply_of_an_unknown_city_is_empty()
    {
        var (uow, stays, _) = CityFixture.Uow();
        stays.All = [CityFixture.Stay(CityFixture.NearHaldwani)];

        var result = CityFixture.Read(await Supply(uow, CityFixture.Resolver(CityFixture.Haldwani), CityFixture.TabsCache(), 999));

        Assert.Equal(200, result.Status);
        Assert.Equal(0, result.Data.GetProperty("Stays").GetInt32());
    }

    [Fact]
    public void Supply_cache_key_holds_the_city_and_no_coordinates()
    {
        var key = TripSupplyCache.CityCountsKey(210000002, "7-1");

        Assert.Equal("trip:supply:c:210000002:7-1", key);
        Assert.DoesNotContain(".", key);
    }

    [Fact]
    public async Task Any_stay_or_taxi_change_moves_the_global_version()
    {
        var before = await TripSupplyCache.AllVersionAsync(null);
        await TripSupplyCache.BumpAsync(null, null);
        await TripSupplyCache.BumpAsync(null, Guid.NewGuid());

        Assert.NotEqual(before, await TripSupplyCache.AllVersionAsync(null));
    }

    private static Task<IResult> Nearest(ICityResolver resolver, double lat, double lng) =>
        TripHandlers.GetNearestCity(lat, lng, CityFixture.Caller(), resolver, new CityFixture.AllowAll(), new DefaultHttpContext());

    [Fact]
    public async Task Nearest_city_endpoint_answers_one_city_a_miss_or_unavailable()
    {
        var r = CityFixture.Resolver(CityFixture.Haldwani, CityFixture.Kathgodam);

        var hit = CityFixture.Read(await Nearest(r, CityFixture.NearKathgodam.Lat, CityFixture.NearKathgodam.Lng)).Data;
        var miss = CityFixture.Read(await Nearest(r, CityFixture.Nowhere.Lat, CityFixture.Nowhere.Lng)).Data;
        var down = CityFixture.Read(await Nearest(CityFixture.NoSnapshot(), 29.2, 79.5)).Data;

        Assert.Equal(CityFixture.Kathgodam.CityKey, hit.GetProperty("CityKey").GetInt64());
        Assert.True(hit.GetProperty("WithinGuard").GetBoolean());
        Assert.True(miss.GetProperty("Available").GetBoolean());
        Assert.False(miss.GetProperty("WithinGuard").GetBoolean());
        Assert.False(down.GetProperty("Available").GetBoolean());
    }
}
