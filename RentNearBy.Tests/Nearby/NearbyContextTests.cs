using Microsoft.Extensions.Caching.Memory;
using RentNearBy.Infrastructure.Services;

namespace RentNearBy.Tests.Nearby;

public class NearbyContextTests
{
    private sealed class FakeCatalog(TripCityCatalogSnapshot? snapshot) : ITripCityCatalog
    {
        public int Calls;
        public Task<TripCityCatalogSnapshot?> GetAsync(CancellationToken ct = default) { Calls++; return Task.FromResult(snapshot); }
        public Task<(bool Available, TripCityInfo? City)> FindAsync(long cityKey, CancellationToken ct = default)
            => Task.FromResult((snapshot != null, snapshot?.Cities.FirstOrDefault(c => c.CityKey == cityKey)));
        public Task InvalidateAsync() => Task.CompletedTask;
    }

    private static TripCityInfo City(long key, string name, string state, double lat, double lng)
        => new() { CityKey = key, Name = name, StateName = state, DistrictName = name, Latitude = lat, Longitude = lng };

    private static readonly TripCityCatalogSnapshot Catalog = new()
    {
        Cities =
        [
            City(1, "Haldwani", "Uttarakhand", 29.2183, 79.5130),
            City(2, "Nainital", "Uttarakhand", 29.3919, 79.4542),
            City(3, "Shimla", "Himachal Pradesh", 31.1048, 77.1734),
        ],
    };

    private static NearbyContext Make(ITripCityCatalog catalog, IReadOnlyList<string>? states = null, Action? onStates = null)
        => new(catalog, new MemoryCache(new MemoryCacheOptions()), _ =>
        {
            onStates?.Invoke();
            return Task.FromResult(states ?? ["Uttarakhand", "Himachal Pradesh"]);
        });

    [Fact]
    public async Task Nearest_city_has_no_cutoff()
    {
        var ctx = Make(new FakeCatalog(Catalog));
        var pune = await ctx.NearestCityAsync(18.5204, 73.8567);
        Assert.NotNull(pune);
        Assert.Equal("Haldwani", pune!.Name);
        Assert.True(pune.DistanceKm > 1000);
    }

    [Fact]
    public async Task Haldwani_centre_gives_haldwani_and_not_far()
    {
        var h = await Make(new FakeCatalog(Catalog)).HeaderAsync(29.2200, 79.5140, 0.4);
        Assert.Equal("Haldwani", h.CentreCityName);
        Assert.Equal("Uttarakhand", h.CentreStateName);
        Assert.False(h.FarAway);
        Assert.False(h.CitiesUnavailable);
        Assert.Equal("We are live in Uttarakhand and Himachal Pradesh", h.LivesIn);
        Assert.InRange(h.CentreCityKm!.Value, 0, 1);
    }

    [Fact]
    public async Task Far_flag_follows_nearest_item_over_15_km()
    {
        var ctx = Make(new FakeCatalog(Catalog));
        Assert.False((await ctx.HeaderAsync(29.2, 79.5, 15.0)).FarAway);
        Assert.True((await ctx.HeaderAsync(29.2, 79.5, 15.1)).FarAway);
        Assert.False((await ctx.HeaderAsync(29.2, 79.5, null)).FarAway);
    }

    [Fact]
    public async Task Catalog_down_still_gives_a_header()
    {
        var ctx = Make(new FakeCatalog(null));
        Assert.Null(await ctx.NearestCityAsync(29.2, 79.5));
        var h = await ctx.HeaderAsync(29.2, 79.5, 3);
        Assert.True(h.CitiesUnavailable);
        Assert.Null(h.CentreCityName);
        Assert.NotEqual("", h.LivesIn);
    }

    [Fact]
    public async Task Live_states_are_read_once_inside_five_minutes()
    {
        var reads = 0;
        var ctx = Make(new FakeCatalog(Catalog), onStates: () => reads++);
        await ctx.HeaderAsync(29.2, 79.5, 1);
        await ctx.HeaderAsync(29.2, 79.5, 1);
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task No_live_states_gives_an_empty_line()
    {
        var h = await Make(new FakeCatalog(Catalog), states: []).HeaderAsync(29.2, 79.5, 1);
        Assert.Equal("", h.LivesIn);
    }
}
