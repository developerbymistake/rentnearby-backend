using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using RentNearBy.Infrastructure.Services;

namespace RentNearBy.Tests.Cities;

public class ParentCityResolverTests(ITestOutputHelper output)
{
    private static TripCityInfo City(long key, string name, double lat, double lng, string district = "Nainital")
        => new() { CityKey = key, Name = name, DistrictName = district, StateName = "Uttarakhand", Latitude = lat, Longitude = lng };

    private static readonly TripCityInfo Haldwani = City(1, "Haldwani", 29.2183, 79.5130);
    private static readonly TripCityInfo Kathgodam = City(2, "Kathgodam", 29.2656, 79.5262);
    private static readonly TripCityInfo Bhimtal = City(3, "Bhimtal", 29.3470, 79.5640);
    private static readonly TripCityInfo Lalkuan = City(4, "Lalkuan", 29.0500, 79.5200);

    private static readonly (double Lat, double Lng) KamalwaganjaPin = (29.2250, 79.5200);

    private sealed class Net(TaxiWaysNetworkSnapshot? s) : ITaxiWaysNetwork
    {
        public Task<TaxiWaysNetworkSnapshot?> GetAsync(CancellationToken ct = default) => Task.FromResult(s);
        public string? CurrentVersion => "t";
    }

    private sealed class FakeGeocoder : IAreaGeocoder
    {
        public readonly List<(double Lat, double Lng, AreaAddress Address)> Places = [];
        public int Calls;
        public bool Down;
        public TimeSpan Delay;

        public async Task<AreaAddress?> ReverseAreaAsync(double lat, double lng, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay);
            if (Down) return null;
            var hit = Places.Where(p => TaxiGeo.DistanceMeters(lat, lng, p.Lat, p.Lng) <= 1500)
                .OrderBy(p => TaxiGeo.DistanceMeters(lat, lng, p.Lat, p.Lng)).Select(p => p.Address).FirstOrDefault();
            return hit ?? new AreaAddress(null, null, null, null);
        }
    }

    private static FakeGeocoder Geo()
    {
        var g = new FakeGeocoder();
        g.Places.Add((Kathgodam.Latitude, Kathgodam.Longitude, new AreaAddress("Haldwani", null, null, "Haldwani")));
        g.Places.Add((KamalwaganjaPin.Lat, KamalwaganjaPin.Lng, new AreaAddress(null, null, "Kamalwaganja", "Haldwani")));
        g.Places.Add((Haldwani.Latitude, Haldwani.Longitude, new AreaAddress("Haldwani", null, null, "Haldwani")));
        g.Places.Add((Bhimtal.Latitude, Bhimtal.Longitude, new AreaAddress(null, "Bhimtal", null, "Bhimtal")));
        g.Places.Add((Lalkuan.Latitude, Lalkuan.Longitude, new AreaAddress(null, null, "Lalkuan", "Lalkuan")));
        return g;
    }

    private static ParentCityResolver Make(FakeGeocoder geo, TimeSpan? timeout = null, params TripCityInfo[] cities)
    {
        var all = cities.Length > 0 ? cities : [Haldwani, Kathgodam, Bhimtal, Lalkuan];
        var net = new Net(TaxiWaysClustering.Build([], all, new TaxiWaysOptions(), "t", DateTime.UtcNow));
        return new ParentCityResolver(net, Options.Create(new TaxiWaysOptions()), geo, new CityResolveCache(new MemoryCache(new MemoryCacheOptions())), timeout);
    }

    private static async Task<string?> NameAt(ParentCityResolver r, double lat, double lng) => (await r.ResolveAsync(lat, lng)).City?.Name;

    [Fact]
    public async Task Kathgodam_is_Haldwani() => Assert.Equal("Haldwani", await NameAt(Make(Geo()), Kathgodam.Latitude, Kathgodam.Longitude));

    [Fact]
    public async Task Kamalwaganja_village_uses_county_Haldwani() => Assert.Equal("Haldwani", await NameAt(Make(Geo()), KamalwaganjaPin.Lat, KamalwaganjaPin.Lng));

    [Fact]
    public async Task Haldwani_is_Haldwani() => Assert.Equal("Haldwani", await NameAt(Make(Geo()), Haldwani.Latitude, Haldwani.Longitude));

    [Fact]
    public async Task Bhimtal_town_is_Bhimtal() => Assert.Equal("Bhimtal", await NameAt(Make(Geo()), Bhimtal.Latitude, Bhimtal.Longitude));

    [Fact]
    public async Task Lalkuan_county_is_Lalkuan() => Assert.Equal("Lalkuan", await NameAt(Make(Geo()), Lalkuan.Latitude, Lalkuan.Longitude));

    [Fact]
    public async Task No_atlas_match_falls_back_to_nearest_city()
    {
        var g = new FakeGeocoder();
        g.Places.Add((Kathgodam.Latitude, Kathgodam.Longitude, new AreaAddress("Nowhereville", null, null, null)));
        Assert.Equal("Kathgodam", await NameAt(Make(g), Kathgodam.Latitude, Kathgodam.Longitude));
    }

    [Fact]
    public async Task A_route_end_takes_only_a_geocoder_named_parent_never_the_nearest_city()
    {
        var snapshot = TaxiWaysClustering.Build([], [Haldwani, Kathgodam, Bhimtal, Lalkuan], new TaxiWaysOptions(), "t", DateTime.UtcNow);
        var g = Geo();
        g.Places.Add((29.0334, 79.4737, new AreaAddress("Pantnagar", null, null, null)));
        var r = Make(g);
        Assert.Equal("Haldwani", (await r.NamedParentOfAsync(snapshot, Kathgodam.Latitude, Kathgodam.Longitude))?.Name);
        Assert.Null(await r.NamedParentOfAsync(snapshot, 29.0334, 79.4737));
        g.Down = true;
        Assert.Null(await Make(g).NamedParentOfAsync(snapshot, 29.1, 79.5));
    }

    [Fact]
    public async Task Name_in_another_district_is_not_matched()
    {
        var other = City(9, "Haldwani", 29.2183, 79.5130, "Other");
        var r = Make(Geo(), null, other, Kathgodam);
        Assert.Equal("Kathgodam", await NameAt(r, Kathgodam.Latitude, Kathgodam.Longitude));
    }

    [Fact]
    public async Task Geocoder_timeout_falls_back_to_nearest_city()
    {
        var g = Geo();
        g.Delay = TimeSpan.FromSeconds(5);
        var r = Make(g, TimeSpan.FromMilliseconds(50));
        Assert.Equal("Kathgodam", await NameAt(r, Kathgodam.Latitude, Kathgodam.Longitude));
    }

    [Fact]
    public async Task Geocoder_down_falls_back_to_nearest_city()
    {
        var g = Geo();
        g.Down = true;
        Assert.Equal("Kathgodam", await NameAt(Make(g), Kathgodam.Latitude, Kathgodam.Longitude));
    }

    [Fact]
    public async Task Pin_outside_every_city_guard_is_no_city_and_skips_the_geocoder()
    {
        var g = Geo();
        var pin = await Make(g).ResolveAsync(22.0, 75.0);
        Assert.True(pin.Available);
        Assert.Null(pin.City);
        Assert.Equal(0, g.Calls);
    }

    [Fact]
    public async Task Cache_hit_does_not_call_the_geocoder()
    {
        var g = Geo();
        var r = Make(g);
        await r.ResolveAsync(Kathgodam.Latitude, Kathgodam.Longitude);
        await r.ResolveAsync(Kathgodam.Latitude, Kathgodam.Longitude);
        Assert.Equal(1, g.Calls);
    }

    [Fact]
    public async Task Pins_in_one_grid_cell_share_one_lookup()
    {
        var g = Geo();
        var r = Make(g);
        var (_, cLat, cLng) = CityResolveCache.CellOf(Kathgodam.Latitude, Kathgodam.Longitude);
        await r.ResolveAsync(cLat + 0.001, cLng + 0.001);
        await r.ResolveAsync(cLat - 0.001, cLng - 0.001);
        Assert.Equal(1, g.Calls);
    }

    [Fact]
    public async Task Failed_lookup_is_not_retried_at_once()
    {
        var g = Geo();
        g.Down = true;
        var r = Make(g);
        await r.ResolveAsync(Kathgodam.Latitude, Kathgodam.Longitude);
        g.Down = false;
        Assert.Equal("Kathgodam", await NameAt(r, Kathgodam.Latitude, Kathgodam.Longitude));
        Assert.Equal(1, g.Calls);
    }

    [Fact]
    public async Task Filter_keeps_items_whose_parent_city_is_the_key()
    {
        var r = Make(Geo());
        var pts = new[] { (Kathgodam.Latitude, Kathgodam.Longitude), KamalwaganjaPin, (Bhimtal.Latitude, Bhimtal.Longitude) };
        var inHaldwani = await r.FilterAsync(pts, p => p, Haldwani.CityKey);
        Assert.Equal(2, inHaldwani.Count);
        Assert.Empty(await r.FilterAsync(pts, p => p, Kathgodam.CityKey));
    }

    [Fact]
    public async Task Mapping_of_a_district_prints_area_to_parent_city()
    {
        var r = Make(Geo());
        var rows = await r.MappingAsync("Nainital");
        output.WriteLine(ParentCityResolver.Format("Nainital", rows));
        Assert.Equal(4, rows.Count);
        Assert.Equal("Haldwani", rows.Single(x => x.Area == "Kathgodam").Parent);
        Assert.Equal("Bhimtal", rows.Single(x => x.Area == "Bhimtal").Parent);
    }
}
