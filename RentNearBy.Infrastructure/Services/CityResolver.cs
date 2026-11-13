using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;

namespace RentNearBy.Infrastructure.Services;

public sealed class CityResolver(ITaxiWaysNetwork network, IOptions<TaxiWaysOptions> options, ParentCityResolver? parent = null) : ICityResolver
{
    private static readonly ConditionalWeakTable<TaxiWaysNetworkSnapshot, Dictionary<long, TripCityInfo>> ByKey = new();

    public double BoxKm => options.Value.CityResolveMeters / 1000;

    private async Task<TaxiWaysNetworkSnapshot?> UsableAsync()
    {
        var snapshot = await network.GetAsync();
        return snapshot == null || snapshot.CityGrid.Count == 0 ? null : snapshot;
    }

    public async Task<CityPin> ResolveAsync(double lat, double lng)
    {
        if (parent != null) return await parent.ResolveAsync(lat, lng);
        if (await UsableAsync() is not { } s) return new CityPin(false, null);
        return new CityPin(true, TaxiWaysSearchEngine.NearestCity(s, lat, lng, options.Value.CityResolveMeters));
    }

    public async Task<CityPin> FindAsync(long cityKey)
    {
        if (await UsableAsync() is not { } s) return new CityPin(false, null);
        var map = ByKey.GetValue(s, snap => snap.CityGrid.Values.SelectMany(l => l).GroupBy(c => c.CityKey).ToDictionary(g => g.Key, g => g.First()));
        return new CityPin(true, map.GetValueOrDefault(cityKey));
    }

    public async Task<IReadOnlyList<T>> FilterAsync<T>(IEnumerable<T> items, Func<T, (double Lat, double Lng)?> point, long cityKey)
    {
        if (parent != null) return await parent.FilterAsync(items, point, cityKey);
        if (await UsableAsync() is not { } s) return [];
        return TaxiWaysSearchEngine.InCity(s, items, point, cityKey, options.Value);
    }

    public Task WarmAsync(double lat, double lng) => parent?.WarmAsync(lat, lng) ?? Task.CompletedTask;
}

public sealed record CityFetch<T>(IReadOnlyList<T> Items, bool Capped);

// A city's stays or taxis: bounded box around the city centre (any district), fetched in pages, cut by the single-city rule.
public static class CityListings
{
    public const int PageSize = 500;
    public const int MaxCityFetch = 5000;

    public static async Task<CityFetch<T>> CollectAsync<T>(
        Func<int, int, Task<IReadOnlyList<T>>> page, Func<IReadOnlyList<T>, Task<IReadOnlyList<T>>> keep)
    {
        var kept = new List<T>();
        var skip = 0;
        while (true)
        {
            var take = Math.Min(PageSize, MaxCityFetch - skip);
            var rows = await page(take, skip);
            kept.AddRange(await keep(rows));
            skip += rows.Count;
            if (rows.Count < take) return new CityFetch<T>(kept, false);
            if (skip >= MaxCityFetch) return new CityFetch<T>(kept, true);
        }
    }

    public static Task<CityFetch<TaxiAt>> TaxisAsync(ITaxiListingRepository taxis, ICityResolver resolver, TripCityInfo city, bool localRateOnly)
        => CollectAsync<TaxiAt>(
            async (take, skip) => (await taxis.BrowseLocalNearAsync(city.Latitude, city.Longitude, resolver.BoxKm, localRateOnly, take, skip))
                .Select(x => new TaxiAt(x.Item, x.Lat, x.Lng)).ToList(),
            async rows => await resolver.FilterAsync(rows, t => (t.Lat, t.Lng), city.CityKey));

    public static Task<CityFetch<StayNearbyDto>> StaysAsync(IStayListingRepository stays, ICityResolver resolver, TripCityInfo city, Guid? stayTypeId)
        => CollectAsync<StayNearbyDto>(
            async (take, skip) => (await stays.GetNearbyAsync(city.Latitude, city.Longitude, resolver.BoxKm, null, stayTypeId, take, skip)).ToList(),
            async rows => await resolver.FilterAsync(rows, s => ((double)s.Latitude, (double)s.Longitude), city.CityKey));
}
