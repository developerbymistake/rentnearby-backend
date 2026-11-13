using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using RentNearBy.Infrastructure.Services;

namespace RentNearBy.Tests.Trips;

// One city rule: an area belongs to its parent city (Kathgodam, Kamalwaganja = Haldwani) for pins, route ends and labels.
public class CityParentTests
{
    private static TripCityInfo City(long key, string name, (double Lat, double Lng) at) =>
        new() { CityKey = key, Name = name, DistrictName = "Nainital", StateName = "Uttarakhand", Latitude = at.Lat, Longitude = at.Lng };

    private static readonly (double Lat, double Lng) Haldwani = (29.2183, 79.5130);
    private static readonly (double Lat, double Lng) Kathgodam = (29.2656, 79.5262);
    private static readonly (double Lat, double Lng) Kamalwaganja = (29.2250, 79.5200);
    private static readonly (double Lat, double Lng) Nainital = (29.3919, 79.4542);
    private static readonly (double Lat, double Lng) Bhimtal = (29.3436, 79.5535);
    private static readonly (double Lat, double Lng) Almora = (29.5971, 79.6591);

    private static readonly TripCityInfo[] Cities =
    [
        City(1, "Haldwani", Haldwani), City(2, "Kathgodam", Kathgodam), City(3, "Nainital", Nainital),
        City(4, "Bhimtal", Bhimtal), City(5, "Almora", Almora),
    ];

    private sealed class Net(TaxiWaysNetworkSnapshot s) : ITaxiWaysNetwork
    {
        public Task<TaxiWaysNetworkSnapshot?> GetAsync(CancellationToken ct = default) => Task.FromResult<TaxiWaysNetworkSnapshot?>(s);
        public string? CurrentVersion => "t";
    }

    private sealed class Geocoder : IAreaGeocoder
    {
        public Task<AreaAddress?> ReverseAreaAsync(double lat, double lng, CancellationToken ct = default)
        {
            AreaAddress a = Near(lat, lng, Kathgodam) ? new("Haldwani", null, null, "Haldwani")
                : Near(lat, lng, Kamalwaganja) ? new(null, null, "Kamalwaganja", "Haldwani")
                : Near(lat, lng, Haldwani) ? new("Haldwani", null, null, "Haldwani")
                : Near(lat, lng, Bhimtal) ? new(null, "Bhimtal", null, "Bhimtal")
                : Near(lat, lng, Nainital) ? new(null, "Nainital", null, "Nainital")
                : new(null, "Almora", null, "Almora");
            return Task.FromResult<AreaAddress?>(a);
        }

        private static bool Near(double lat, double lng, (double Lat, double Lng) p) => TaxiGeo.DistanceMeters(lat, lng, p.Lat, p.Lng) < 1500;
    }

    private static (TaxiWaysNetworkSnapshot Plain, ParentCityResolver Parent) Parent()
    {
        var plain = TaxiWaysClustering.Build([], Cities, TaxiWaysFixture.Options, "t", DateTime.UtcNow);
        var parent = new ParentCityResolver(new Net(plain), Options.Create(TaxiWaysFixture.Options), new Geocoder(),
            new CityResolveCache(new MemoryCache(new MemoryCacheOptions())));
        return (plain, parent);
    }

    private static async Task<TaxiWaysNetworkSnapshot> Refined(ParentCityResolver parent, TaxiWaysNetworkSnapshot plain, params TaxiWaysSourceRow[] rows)
    {
        var first = TaxiWaysClustering.Build(rows, Cities, TaxiWaysFixture.Options, "t", DateTime.UtcNow);
        var map = new Dictionary<(double, double), TripCityInfo?>();
        foreach (var r in rows)
            foreach (var e in new[] { (r.FromLat, r.FromLng), (r.ToLat, r.ToLng) })
                map[e] = await parent.ParentOfAsync(first, e.Item1, e.Item2);
        return TaxiWaysClustering.Build(rows, Cities, TaxiWaysFixture.Options, "t", DateTime.UtcNow, endCity: (la, ln) => map.GetValueOrDefault((la, ln)));
    }

    private static async Task<HashSet<Guid>> Cars(ParentCityResolver parent, TaxiWaysNetworkSnapshot s, (double Lat, double Lng) from, (double Lat, double Lng) to)
    {
        var a = TaxiWaysSearchEngine.Resolve(s, from.Lat, from.Lng, false, TaxiWaysFixture.Options, null, await parent.ResolveAsync(from.Lat, from.Lng));
        var b = TaxiWaysSearchEngine.Resolve(s, to.Lat, to.Lng, false, TaxiWaysFixture.Options, null, await parent.ResolveAsync(to.Lat, to.Lng));
        var r = TaxiWaysSearchEngine.Search(s, a, b, 4, TaxiWaysFixture.Options);
        return r.AllTheWay.Select(c => c.Driver.TaxiId).Concat(r.Plans.SelectMany(p => p.Legs.SelectMany(l => l.Drivers.Select(d => d.Driver.TaxiId)))).ToHashSet();
    }

    [Theory]
    [InlineData(29.2183, 79.5130)]
    [InlineData(29.2250, 79.5200)]
    public async Task Haldwani_traveller_sees_one_car_never_Haldwani_to_Kathgodam(double lat, double lng)
    {
        var (plain, parent) = Parent();
        var hk = TaxiWaysFixture.Row("Haldwani", Haldwani, "Kathgodam", Kathgodam, 400);
        var ka = TaxiWaysFixture.Row("Kathgodam", Kathgodam, "Almora", Almora, 2500);
        var s = await Refined(parent, plain, hk, ka);

        var a = TaxiWaysSearchEngine.Resolve(s, lat, lng, false, TaxiWaysFixture.Options, null, await parent.ResolveAsync(lat, lng));
        var b = TaxiWaysSearchEngine.Resolve(s, Almora.Lat, Almora.Lng, false, TaxiWaysFixture.Options, null, await parent.ResolveAsync(Almora.Lat, Almora.Lng));
        var r = TaxiWaysSearchEngine.Search(s, a, b, 4, TaxiWaysFixture.Options);

        Assert.Equal(new HashSet<Guid> { ka.TaxiId }, r.AllTheWay.Select(c => c.Driver.TaxiId).ToHashSet());
        Assert.Empty(r.Plans);
        Assert.DoesNotContain(hk.TaxiId, await Cars(parent, s, (lat, lng), Almora));
    }

    [Fact]
    public async Task Bhimtal_driver_is_not_shown_for_Haldwani_to_Nainital()
    {
        var (plain, parent) = Parent();
        var bn = TaxiWaysFixture.Row("Bhimtal", Bhimtal, "Nainital", Nainital, 600);
        var hn = TaxiWaysFixture.Row("Haldwani", Haldwani, "Nainital", Nainital, 1500);
        var s = await Refined(parent, plain, bn, hn);

        var cars = await Cars(parent, s, Haldwani, Nainital);

        Assert.Contains(hn.TaxiId, cars);
        Assert.DoesNotContain(bn.TaxiId, cars);
    }

    [Fact]
    public async Task Guide_origin_label_and_ways_origin_label_come_from_the_same_resolver()
    {
        var (plain, parent) = Parent();
        var resolver = new CityResolver(new Net(plain), Options.Create(TaxiWaysFixture.Options), parent);
        var catalog = new FakeCatalog(new TripCityCatalogSnapshot { Cities = [.. Cities] });
        var ctx = new NearbyContext(catalog, new MemoryCache(new MemoryCacheOptions()), _ => Task.FromResult<IReadOnlyList<string>>(["Uttarakhand"]), resolver);

        var guide = await ctx.NearestCityAsync(Kamalwaganja.Lat, Kamalwaganja.Lng);
        var pin = await resolver.ResolveAsync(Kamalwaganja.Lat, Kamalwaganja.Lng);
        var ways = TaxiWaysSearchEngine.Resolve(plain, Kamalwaganja.Lat, Kamalwaganja.Lng, false, TaxiWaysFixture.Options, null, pin);
        var header = await ctx.HeaderAsync(Kamalwaganja.Lat, Kamalwaganja.Lng, null);

        Assert.Equal("Haldwani", guide?.Name);
        Assert.Equal(guide?.Name, ways.Name);
        Assert.Equal(guide?.Name, header.CentreCityName);
    }

    private sealed class FakeCatalog(TripCityCatalogSnapshot snapshot) : ITripCityCatalog
    {
        public Task<TripCityCatalogSnapshot?> GetAsync(CancellationToken ct = default) => Task.FromResult<TripCityCatalogSnapshot?>(snapshot);
        public Task<(bool Available, TripCityInfo? City)> FindAsync(long cityKey, CancellationToken ct = default)
            => Task.FromResult((true, snapshot.Cities.FirstOrDefault(c => c.CityKey == cityKey)));
        public Task InvalidateAsync() => Task.CompletedTask;
    }
}
