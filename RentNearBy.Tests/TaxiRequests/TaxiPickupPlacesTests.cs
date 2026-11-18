using RentNearBy.Core.DTOs.TaxiRequests;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Services.TaxiRequests;

namespace RentNearBy.Tests.TaxiRequests;

// T1 pieces that need no database: seats, the switch flag, the pool and same-city rules in the SQL, sort, and the cache.
public class TaxiPickupPlacesTests
{
    private static readonly TaxiPickupLists NoLists = new([], [], 0);

    private sealed class Resolver(params (long Key, string Name, double Lat, double Lng)[] cities) : ICityResolver
    {
        public double BoxKm => 25;
        public Task<CityPin> ResolveAsync(double lat, double lng) => throw new NotSupportedException();
        public Task<CityPin> FindAsync(long cityKey)
        {
            var c = cities.FirstOrDefault(x => x.Key == cityKey);
            return Task.FromResult(c.Name is null
                ? new CityPin(false, null)
                : new CityPin(true, new TripCityInfo { CityKey = c.Key, Name = c.Name, Latitude = c.Lat, Longitude = c.Lng }));
        }
        public Task<IReadOnlyList<T>> FilterAsync<T>(IEnumerable<T> items, Func<T, (double Lat, double Lng)?> point, long cityKey) => throw new NotSupportedException();
        public Task WarmAsync(double lat, double lng) => Task.CompletedTask;
    }

    private sealed class FakeStore : ITaxiPickupCacheStore
    {
        public Dictionary<string, string> Values { get; } = new();
        public long Generation { get; set; }
        public bool Down { get; set; }
        public int Gets { get; private set; }
        private void Check() { if (Down) throw new TimeoutException("redis down"); }
        public Task<long> GenerationAsync() { Check(); return Task.FromResult(Generation); }
        public Task<string?> GetAsync(string key) { Check(); Gets++; return Task.FromResult(Values.GetValueOrDefault(key)); }
        public Task SetAsync(string key, string value, TimeSpan ttl) { Check(); Values[key] = value; return Task.CompletedTask; }
        public Task BumpAsync() { Check(); Generation++; return Task.CompletedTask; }
    }

    [Theory]
    [InlineData(1, 4)]
    [InlineData(4, 4)]
    [InlineData(5, 6)]
    [InlineData(6, 6)]
    [InlineData(7, 7)]
    [InlineData(12, 7)]
    public void Suggested_seats_by_travellers(int travellers, int seats)
    {
        var dto = TaxiPickupPlaces.ToDto(NoLists, null, null, true, travellers);
        Assert.Equal(seats, dto.SuggestedSeats);
        Assert.Equal(travellers, dto.Travellers);
    }

    [Fact]
    public void Same_city_route_is_not_a_pickup()
    {
        Assert.Contains("rt.\"FromCityKey\" <> rt.\"ToCityKey\"", TaxiPickupPlaces.FromCitiesSql);
        Assert.Contains("rt.\"FromCityKey\" IS NOT NULL", TaxiPickupPlaces.FromCitiesSql);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Paused_flag_follows_the_switch(bool switchOn, bool paused)
        => Assert.Equal(paused, TaxiPickupPlaces.ToDto(NoLists, null, null, switchOn, 2).Paused);

    [Fact]
    public void Driver_at_the_missed_rings_limit_is_not_counted()
    {
        Assert.Contains($"l.\"RingMissStreak\" < {TaxiDispatchRules.RingMissLimit}", TaxiCandidateQuery.StaticRule);
        Assert.Contains(TaxiCandidateQuery.StaticRule, TaxiPickupPlaces.FromCitiesSql);
        Assert.Contains(TaxiCandidateQuery.StaticRule, TaxiPickupPlaces.DriversToCitySql);
    }

    [Fact]
    public void Dto_carries_both_lists_the_cities_and_the_sightseeing_count()
    {
        var lists = new TaxiPickupLists([new TaxiPickupPlace(1, "Kathgodam", 6)], [new TaxiPickupPlace(2, "Ramnagar", 5)], 15);
        var dto = TaxiPickupPlaces.ToDto(lists, new TaxiCityRefDto { CityKey = 9, Name = "Nainital" }, new TaxiCityRefDto { CityKey = 8, Name = "Ranikhet" }, true, 2);
        Assert.Equal(("Kathgodam", 6), (dto.Pickup[0].Name, dto.Pickup[0].Drivers));
        Assert.Equal(("Ramnagar", 5), (dto.Drop[0].Name, dto.Drop[0].Drivers));
        Assert.Equal(15, dto.SightseeingDrivers);
        Assert.Equal("Nainital", dto.FirstCity!.Name);
        Assert.Equal("Ranikhet", dto.LastCity!.Name);
    }

    [Fact]
    public async Task Lists_are_sorted_by_distance_from_the_trip_city_then_name()
    {
        var resolver = new Resolver((10, "Nainital", 29.39, 79.45), (1, "Zeta Far", 30.5, 79.5), (2, "Alpha Mid", 29.8, 79.5),
            (3, "Beta Near", 29.4, 79.46), (4, "Aaa Near", 29.4, 79.46));
        var rows = new[] { Row(1), Row(2), Row(3), Row(4) };
        var lists = await TaxiPickupPlaces.BuildAsync(resolver, rows, [], 0, firstCityKey: 10, lastCityKey: 10);
        Assert.Equal(["Aaa Near", "Beta Near", "Alpha Mid", "Zeta Far"], lists.Pickup.Select(p => p.Name));
    }

    private static TaxiPickupPlaces.FromCityRow Row(long key) => new() { CityKey = key, Drivers = 1 };

    [Fact]
    public async Task Cache_hit_skips_the_database_until_the_generation_moves()
    {
        var store = new FakeStore();
        var cache = new TaxiPickupCache(store);
        var loads = 0;
        Task<int> Load() { loads++; return Task.FromResult(7); }

        Assert.Equal(7, await cache.GetOrLoadAsync("cnt", 5, Load));
        Assert.Equal(7, await cache.GetOrLoadAsync("cnt", 5, Load));
        Assert.Equal(1, loads);

        await cache.BumpAsync();
        Assert.Equal(7, await cache.GetOrLoadAsync("cnt", 5, Load));
        Assert.Equal(2, loads);
    }

    [Fact]
    public async Task Redis_down_falls_back_to_the_database()
    {
        var store = new FakeStore { Down = true };
        var cache = new TaxiPickupCache(store);
        var loads = 0;

        var value = await cache.GetOrLoadAsync("cnt", 5, () => { loads++; return Task.FromResult(3); });
        await cache.BumpAsync();

        Assert.Equal(3, value);
        Assert.Equal(1, loads);
        Assert.Empty(store.Values);
    }

    [Fact]
    public async Task Missing_store_means_the_database_answers_every_time()
    {
        var cache = new TaxiPickupCache(null);
        var loads = 0;
        for (var i = 0; i < 3; i++) await cache.GetOrLoadAsync("cnt", 5, () => { loads++; return Task.FromResult(1); });
        Assert.Equal(3, loads);
        await cache.BumpAsync();
    }

    [Fact]
    public async Task A_broken_cache_entry_is_a_miss()
    {
        var store = new FakeStore();
        store.Values[TaxiPickupCache.KeyOf("cnt", 0, 5)] = "not json";
        var value = await new TaxiPickupCache(store).GetOrLoadAsync("cnt", 5, () => Task.FromResult(9));
        Assert.Equal(9, value);
    }

    [Fact]
    public async Task Rows_survive_the_cache_round_trip()
    {
        var store = new FakeStore();
        var cache = new TaxiPickupCache(store);
        await cache.GetOrLoadAsync("from", 5, () => Task.FromResult(new List<TaxiPickupPlaces.FromCityRow> { Row(1), Row(2) }));
        var hit = await cache.GetOrLoadAsync("from", 5, () => Task.FromResult(new List<TaxiPickupPlaces.FromCityRow>()));
        Assert.Equal([1L, 2L], hit.Select(r => r.CityKey));
    }
}
