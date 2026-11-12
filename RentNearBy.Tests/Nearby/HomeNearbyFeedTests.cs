using Microsoft.AspNetCore.Http;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using RentNearBy.Api.Handlers;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Tests.Trips;

namespace RentNearBy.Tests.Nearby;

public class HomeNearbyFeedTests
{
    private sealed class Counts { public int Stays, Taxis, Recent; }

    private static MemoryCache Tabs(params string[] off)
    {
        var memory = new MemoryCache(new MemoryCacheOptions());
        memory.Set(AppTabCache.Key, off.Select(k => new AppTabDto { TabKey = k, IsActive = false }).ToList());
        return memory;
    }

    private static (HomeNearbyHandlers.Loaders Loaders, Counts Counts, FakeNearbyQuery Query, FakeNearbyContext Ctx) Fixture(int stays = 7, int taxis = 7)
    {
        var counts = new Counts();
        var rows = Enumerable.Range(0, Math.Max(stays, taxis)).Select(i => new NearbyRow(Guid.NewGuid(), (i + 1) * 1000.0)).ToList();
        var loaders = new HomeNearbyHandlers.Loaders(
            page =>
            {
                counts.Stays++;
                return Task.FromResult(page.ToDictionary(r => r.Id, r => new StayFeedItemDto { Id = r.Id, Name = "Stay", DistanceKm = NearbyRules.StayKm(r.DistanceMeters) }));
            },
            page =>
            {
                counts.Taxis++;
                return Task.FromResult(page.ToDictionary(r => r.Id, r => new TaxiFeedItemDto { Id = r.Id, DriverName = "Driver", DistanceKm = NearbyRules.TaxiKm(r.DistanceMeters) }));
            },
            take =>
            {
                counts.Recent++;
                return Task.FromResult(Enumerable.Range(0, 20).Select(_ => new StayBrowseDto { Id = Guid.NewGuid() }).ToList());
            });
        return (loaders, counts, new FakeNearbyQuery(rows), new FakeNearbyContext());
    }

    private static Task<IResult> Run(MemoryCache cache, HomeNearbyHandlers.Loaders l, FakeNearbyQuery q, FakeNearbyContext c,
        double? lat = 29.2, double? lng = 79.5, int? limit = null, int? recent = null, string? sections = null)
        => HomeNearbyHandlers.BuildAsync(lat, lng, limit, recent, sections, CityFixture.Uow().Uow, cache, q, c, l);

    [Fact]
    public async Task All_sections_come_back_with_the_header_and_default_limit_five()
    {
        var (l, counts, q, c) = Fixture();

        var r = CityFixture.Read(await Run(Tabs(), l, q, c));

        Assert.Equal(200, r.Status);
        Assert.Equal(5, r.Data.GetProperty("Stays").GetArrayLength());
        Assert.Equal(5, r.Data.GetProperty("Taxis").GetArrayLength());
        Assert.Equal(10, r.Data.GetProperty("RecentStays").GetArrayLength());
        Assert.Equal("Haldwani", r.Data.GetProperty("Header").GetProperty("CentreCityName").GetString());
        Assert.All(q.Calls, call => Assert.Null(call.After));
        Assert.Equal(1, c.HeaderCalls);
    }

    [Fact]
    public async Task Off_tab_is_null_and_skipped_before_any_query()
    {
        var (l, counts, q, c) = Fixture();

        var r = CityFixture.Read(await Run(Tabs(AppTabKeys.Taxi), l, q, c));

        Assert.Equal(JsonValueKind.Null, r.Data.GetProperty("Taxis").ValueKind);
        Assert.Equal(0, counts.Taxis);
        Assert.DoesNotContain(q.Calls, call => call.Kind == NearbyKind.Taxi);
        Assert.False(r.Data.GetProperty("Tabs").GetProperty("Taxi").GetBoolean());
        Assert.Equal(5, r.Data.GetProperty("Stays").GetArrayLength());
    }

    [Fact]
    public async Task Stays_tab_off_skips_stays_and_recent_and_the_header_comes_from_taxis()
    {
        var (l, counts, q, c) = Fixture();

        var r = CityFixture.Read(await Run(Tabs(AppTabKeys.Stays), l, q, c));

        Assert.Equal(JsonValueKind.Null, r.Data.GetProperty("Stays").ValueKind);
        Assert.Equal(JsonValueKind.Null, r.Data.GetProperty("RecentStays").ValueKind);
        Assert.Equal(0, counts.Stays + counts.Recent);
        Assert.Equal(5.0, c.SeenNearestKm);
        Assert.Equal(JsonValueKind.Object, r.Data.GetProperty("Header").ValueKind);
    }

    [Fact]
    public async Task Sections_filter_only_loads_what_is_asked()
    {
        var (l, counts, q, c) = Fixture();

        var r = CityFixture.Read(await Run(Tabs(), l, q, c, sections: "taxis"));

        Assert.Equal(JsonValueKind.Null, r.Data.GetProperty("Stays").ValueKind);
        Assert.Equal(JsonValueKind.Null, r.Data.GetProperty("RecentStays").ValueKind);
        Assert.Equal(5, r.Data.GetProperty("Taxis").GetArrayLength());
        Assert.Equal(0, counts.Stays + counts.Recent);
    }

    [Fact]
    public async Task No_section_on_gives_no_header_and_no_query()
    {
        var (l, _, q, c) = Fixture();

        var r = CityFixture.Read(await Run(Tabs(AppTabKeys.Stays, AppTabKeys.Taxi), l, q, c));

        Assert.Equal(JsonValueKind.Null, r.Data.GetProperty("Header").ValueKind);
        Assert.Empty(q.Calls);
        Assert.Equal(0, c.HeaderCalls);
    }

    [Fact]
    public async Task Limits_are_clamped()
    {
        var (l, _, q, c) = Fixture(stays: 15, taxis: 15);

        var big = CityFixture.Read(await Run(Tabs(), l, q, c, limit: 99, recent: 99));
        var small = CityFixture.Read(await Run(Tabs(), l, q, c, limit: 0, recent: 0));

        Assert.Equal(10, big.Data.GetProperty("Stays").GetArrayLength());
        Assert.Equal(20, big.Data.GetProperty("RecentStays").GetArrayLength());
        Assert.Equal(1, small.Data.GetProperty("Stays").GetArrayLength());
        Assert.Equal(1, small.Data.GetProperty("RecentStays").GetArrayLength());
    }

    [Fact]
    public async Task The_whole_system_empty_gives_empty_rails_not_an_error()
    {
        var counts = new Counts();
        var l = new HomeNearbyHandlers.Loaders(_ => Task.FromResult(new Dictionary<Guid, StayFeedItemDto>()),
            _ => Task.FromResult(new Dictionary<Guid, TaxiFeedItemDto>()), _ => Task.FromResult(new List<StayBrowseDto>()));

        var r = CityFixture.Read(await Run(Tabs(), l, new FakeNearbyQuery([]), new FakeNearbyContext()));

        Assert.Equal(200, r.Status);
        Assert.Equal(0, r.Data.GetProperty("Stays").GetArrayLength());
        Assert.Equal(0, r.Data.GetProperty("Taxis").GetArrayLength());
    }

    [Fact]
    public async Task Bad_location_is_a_400_before_any_work()
    {
        var (l, counts, q, c) = Fixture();

        Assert.Equal(400, CityFixture.Read(await Run(Tabs(), l, q, c, lat: null, lng: 79)).Status);
        Assert.Equal(400, CityFixture.Read(await Run(Tabs(), l, q, c, lat: 29, lng: 181)).Status);
        Assert.Empty(q.Calls);
    }

    [Fact]
    public async Task Response_is_a_superset_that_parses_as_the_old_home_feed()
    {
        var (l, _, q, c) = Fixture();
        var r = CityFixture.Read(await Run(Tabs(), l, q, c));

        var old = JsonSerializer.Deserialize<HomeFeedDto>(r.Data.GetRawText())!;

        Assert.Equal(5, old.Stays!.Count);
        Assert.Equal(5, old.Taxis!.Count);
        Assert.Equal(10, old.RecentStays!.Count);
        Assert.True(old.Tabs.Stays && old.Tabs.Taxi && old.Tabs.Jobs);
    }

    [Fact]
    public async Task Taxi_items_never_carry_coordinates()
    {
        var (l, _, q, c) = Fixture();
        var r = CityFixture.Read(await Run(Tabs(), l, q, c));

        var taxi = r.Data.GetProperty("Taxis")[0];

        Assert.False(taxi.TryGetProperty("Latitude", out _));
        Assert.False(taxi.TryGetProperty("Longitude", out _));
    }
}
