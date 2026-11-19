using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Services.TaxiRequests;

namespace RentNearBy.Tests.TaxiRequests;

// Contract T1 lists: SQL shape (same-city routes left out, list rule applied) and catalog naming (non-live keys dropped).
public class TaxiPickupPlacesBuildTests
{
    private const long Kathgodam = 210000050, Haldwani = 210000060, Gone = 210000099;

    private sealed class Resolver(params (long Key, string Name)[] live) : ICityResolver
    {
        private readonly Dictionary<long, string> _names = live.ToDictionary(x => x.Key, x => x.Name);
        public double BoxKm => 25;
        public Task<CityPin> ResolveAsync(double lat, double lng) => throw new NotSupportedException();
        public Task<CityPin> FindAsync(long cityKey)
            => Task.FromResult(_names.TryGetValue(cityKey, out var n) ? new CityPin(true, new TripCityInfo { CityKey = cityKey, Name = n }) : new CityPin(false, null));
        public Task<IReadOnlyList<T>> FilterAsync<T>(IEnumerable<T> items, Func<T, (double Lat, double Lng)?> point, long cityKey) => throw new NotSupportedException();
        public Task WarmAsync(double lat, double lng) => Task.CompletedTask;
    }

    private static TaxiPickupPlaces.FromCityRow Row(long key, int drivers) => new() { CityKey = key, Drivers = drivers };

    [Fact]
    public void From_cities_sql_leaves_out_routes_inside_one_city_and_applies_the_list_rule()
    {
        Assert.Contains("rt.\"FromCityKey\" <> rt.\"ToCityKey\"", TaxiPickupPlaces.FromCitiesSql);
        Assert.Contains("rt.\"ToCityKey\" = @to", TaxiPickupPlaces.FromCitiesSql);
        Assert.Contains(TaxiCandidateQuery.ListRule, TaxiPickupPlaces.FromCitiesSql);
        Assert.Contains("count(DISTINCT l.\"Id\")", TaxiPickupPlaces.FromCitiesSql);
    }

    [Fact]
    public void Drivers_to_city_sql_applies_the_list_rule()
    {
        Assert.Contains("rt.\"ToCityKey\" = @to", TaxiPickupPlaces.DriversToCitySql);
        Assert.Contains(TaxiCandidateQuery.ListRule, TaxiPickupPlaces.DriversToCitySql);
    }

    [Fact]
    public async Task A_key_that_is_not_a_live_city_is_left_out_of_both_lists()
    {
        var lists = await TaxiPickupPlaces.BuildAsync(new Resolver((Kathgodam, "Kathgodam"), (Haldwani, "Haldwani")),
            [Row(Kathgodam, 3), Row(Gone, 5), Row(Haldwani, 1)], [Row(Gone, 2)], 7);

        Assert.Equal([Haldwani, Kathgodam], lists.PickupKeys);
        Assert.Empty(lists.DropKeys);
        Assert.Equal(3, lists.Pickup.Single(p => p.CityKey == Kathgodam).Drivers);
        Assert.Null(lists.PickupName(Gone));
        Assert.Equal("Kathgodam", lists.PickupName(Kathgodam));
        Assert.Equal(7, lists.SightseeingDrivers);
    }
}
