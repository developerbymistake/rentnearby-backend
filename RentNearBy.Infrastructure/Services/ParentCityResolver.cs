using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;

namespace RentNearBy.Infrastructure.Services;

public enum ParentSource { NameMatch, NearestNoMatch, NearestGeocoderFailed, NoCity }

public sealed record AreaParentRow(string Area, string? Parent, ParentSource Source);

// An area belongs to its parent city: Nominatim's city/town/county name matched to a live Atlas city of the same district,
// else the single nearest Atlas city, else no city (the place itself).
public sealed class ParentCityResolver(
    ITaxiWaysNetwork network, IOptions<TaxiWaysOptions> options, IAreaGeocoder geocoder, CityResolveCache cache, TimeSpan? timeout = null)
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMilliseconds(800);
    private const int Parallel = 8;
    private static readonly string[] Suffixes = [" tehsil", " district", " taluka", " taluk", " municipality"];
    private static readonly ConditionalWeakTable<TaxiWaysNetworkSnapshot, Dictionary<(string District, string Name), TripCityInfo>> Index = new();

    private TimeSpan Timeout => timeout ?? DefaultTimeout;

    public static string? PickName(AreaAddress? a)
    {
        if (a == null) return null;
        foreach (var v in new[] { a.City, a.Town, a.County })
            if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
        return string.Empty;
    }

    public static string Normalize(string name)
    {
        var n = string.Join(' ', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
        foreach (var suffix in Suffixes)
            if (n.EndsWith(suffix, StringComparison.Ordinal) && n.Length > suffix.Length) { n = n[..^suffix.Length]; break; }
        return n;
    }

    private async Task<TaxiWaysNetworkSnapshot?> UsableAsync()
    {
        var s = await network.GetAsync();
        return s == null || s.CityGrid.Count == 0 ? null : s;
    }

    private async Task<string?> FetchNameAsync(double lat, double lng)
    {
        using var cts = new CancellationTokenSource(Timeout);
        try { return PickName(await geocoder.ReverseAreaAsync(lat, lng, cts.Token).WaitAsync(cts.Token)); }
        catch { return null; }
    }

    private static TripCityInfo? Match(TaxiWaysNetworkSnapshot s, string district, string name)
    {
        var index = Index.GetValue(s, snap =>
        {
            var map = new Dictionary<(string District, string Name), TripCityInfo>();
            foreach (var c in snap.CityGrid.Values.SelectMany(l => l).DistinctBy(c => c.CityKey).OrderBy(c => c.CityKey))
                map.TryAdd((Normalize(c.DistrictName), Normalize(c.Name)), c);
            return map;
        });
        return index.GetValueOrDefault((Normalize(district), Normalize(name)));
    }

    private async Task<(TripCityInfo? City, ParentSource Source)> ParentAsync(TaxiWaysNetworkSnapshot s, double lat, double lng)
    {
        var near = TaxiWaysSearchEngine.NearestCity(s, lat, lng, options.Value.CityResolveMeters);
        if (near == null) return (null, ParentSource.NoCity);
        var name = await cache.GetOrFetchAsync(lat, lng, FetchNameAsync);
        if (name == null) return (near, ParentSource.NearestGeocoderFailed);
        if (name.Length > 0 && Match(s, near.DistrictName, name) is { } parent) return (parent, ParentSource.NameMatch);
        return (near, ParentSource.NearestNoMatch);
    }

    public async Task<TripCityInfo?> ParentOfAsync(TaxiWaysNetworkSnapshot s, double lat, double lng) => (await ParentAsync(s, lat, lng)).City;

    // The city the geocoder names as the parent of a point, only when it matches a live city of the district; never the nearest-city fallback.
    public async Task<TripCityInfo?> NamedParentOfAsync(TaxiWaysNetworkSnapshot s, double lat, double lng)
    {
        var (city, source) = await ParentAsync(s, lat, lng);
        return source == ParentSource.NameMatch ? city : null;
    }

    public async Task<CityPin> ResolveAsync(double lat, double lng)
    {
        if (await UsableAsync() is not { } s) return new CityPin(false, null);
        return new CityPin(true, (await ParentAsync(s, lat, lng)).City);
    }

    // Items whose parent city is cityKey. One lookup per grid cell, a few at a time.
    public async Task<IReadOnlyList<T>> FilterAsync<T>(IEnumerable<T> items, Func<T, (double Lat, double Lng)?> point, long cityKey)
    {
        if (await UsableAsync() is not { } s) return [];
        var located = new List<(T Item, (double Lat, double Lng) P, string Cell)>();
        foreach (var item in items)
            if (point(item) is { } p) located.Add((item, p, CityResolveCache.CellOf(p.Lat, p.Lng).Key));
        var cells = located.GroupBy(x => x.Cell).ToDictionary(g => g.Key, g => g.First().P);
        var parentOf = new ConcurrentDictionary<string, long?>();
        using var gate = new SemaphoreSlim(Parallel);
        await Task.WhenAll(cells.Select(async kv =>
        {
            await gate.WaitAsync();
            try { parentOf[kv.Key] = (await ParentAsync(s, kv.Value.Lat, kv.Value.Lng)).City?.CityKey; }
            finally { gate.Release(); }
        }));
        return located.Where(x => parentOf.GetValueOrDefault(x.Cell) == cityKey).Select(x => x.Item).ToList();
    }

    public async Task WarmAsync(double lat, double lng)
    {
        try { await ResolveAsync(lat, lng); }
        catch { }
    }

    // Every live city of a district with the parent city it resolves to, for checking the mapping by eye.
    public async Task<IReadOnlyList<AreaParentRow>> MappingAsync(string districtName)
    {
        if (await UsableAsync() is not { } s) return [];
        var wanted = Normalize(districtName);
        var cities = s.CityGrid.Values.SelectMany(l => l).DistinctBy(c => c.CityKey)
            .Where(c => Normalize(c.DistrictName) == wanted).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var rows = new List<AreaParentRow>();
        foreach (var c in cities)
        {
            var (parent, source) = await ParentAsync(s, c.Latitude, c.Longitude);
            rows.Add(new AreaParentRow(c.Name, parent?.Name, source));
        }
        return rows;
    }

    public static string Format(string districtName, IReadOnlyList<AreaParentRow> rows)
        => $"District {districtName}: {rows.Count} cities\n" + string.Join('\n', rows.Select(r =>
            $"  {r.Area} -> {r.Parent ?? "(itself)"}  [{r.Source}]"));
}
