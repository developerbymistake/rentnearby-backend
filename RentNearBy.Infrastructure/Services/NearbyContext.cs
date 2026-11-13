using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services;

public sealed class NearbyContext : INearbyContext
{
    private const string StatesKey = "nearby:live_states";
    private static readonly TimeSpan StatesTtl = TimeSpan.FromMinutes(5);

    private readonly ICityResolver? _resolver;
    private readonly ITripCityCatalog _catalog;
    private readonly IMemoryCache _cache;
    private readonly Func<CancellationToken, Task<IReadOnlyList<string>>> _states;

    public NearbyContext(ITripCityCatalog catalog, IServiceScopeFactory scopes, IMemoryCache cache, ILogger<NearbyContext> logger, ICityResolver? resolver = null)
        : this(catalog, cache, async ct =>
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                return await db.Districts.AsNoTracking().Where(d => d.IsActive)
                    .Select(d => d.StateName).Distinct().OrderBy(n => n).ToListAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "nearby.states.failed");
                return Array.Empty<string>();
            }
        }, resolver)
    {
    }

    internal NearbyContext(ITripCityCatalog catalog, IMemoryCache cache, Func<CancellationToken, Task<IReadOnlyList<string>>> states, ICityResolver? resolver = null)
    {
        _resolver = resolver;
        _catalog = catalog;
        _cache = cache;
        _states = states;
    }

    public async Task<NearbyCity?> NearestCityAsync(double lat, double lng, CancellationToken ct = default)
        => await CityOfAsync(await _catalog.GetAsync(ct), lat, lng);

    public async Task<NearbyHeaderDto> HeaderAsync(double lat, double lng, double? nearestKm, CancellationToken ct = default)
    {
        var snapshot = await _catalog.GetAsync(ct);
        var city = await CityOfAsync(snapshot, lat, lng);
        var states = await LiveStatesAsync(ct);
        return new NearbyHeaderDto
        {
            CentreCityName = city?.Name,
            CentreStateName = city?.StateName,
            CentreCityKm = city == null ? null : Math.Round(city.DistanceKm, 1),
            LivesIn = NearbyRules.LivesInLine(states),
            FarAway = nearestKm is > NearbyRules.NearKm,
            NearestKm = nearestKm,
            CitiesUnavailable = snapshot == null,
        };
    }

    private async Task<IReadOnlyList<string>> LiveStatesAsync(CancellationToken ct)
    {
        if (_cache.TryGetValue(StatesKey, out IReadOnlyList<string>? hit) && hit != null) return hit;
        var list = await _states(ct);
        if (list.Count > 0) _cache.Set(StatesKey, list, StatesTtl);
        return list;
    }

    // The one city rule: the parent city of the pin (ICityResolver); the plain nearest city only when it has no answer.
    private async Task<NearbyCity?> CityOfAsync(TripCityCatalogSnapshot? snapshot, double lat, double lng)
    {
        if (_resolver != null)
        {
            try
            {
                var pin = await _resolver.ResolveAsync(lat, lng);
                if (pin.City is { } c)
                    return new NearbyCity(c.CityKey, c.Name, c.StateName, c.DistrictName, c.Latitude, c.Longitude, HaversineKm(lat, lng, c.Latitude, c.Longitude));
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
        }
        return Nearest(snapshot, lat, lng);
    }

    private static NearbyCity? Nearest(TripCityCatalogSnapshot? snapshot, double lat, double lng)
    {
        if (snapshot == null || snapshot.Cities.Count == 0) return null;
        TripCityInfo? best = null;
        var bestKm = double.MaxValue;
        foreach (var c in snapshot.Cities)
        {
            var km = HaversineKm(lat, lng, c.Latitude, c.Longitude);
            if (km < bestKm) { bestKm = km; best = c; }
        }
        return best == null ? null
            : new NearbyCity(best.CityKey, best.Name, best.StateName, best.DistrictName, best.Latitude, best.Longitude, bestKm);
    }

    internal static double HaversineKm(double lat1, double lng1, double lat2, double lng2)
    {
        const double R = 6371.0088;
        var dLat = (lat2 - lat1) * Math.PI / 180.0;
        var dLng = (lng2 - lng1) * Math.PI / 180.0;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
            + Math.Cos(lat1 * Math.PI / 180.0) * Math.Cos(lat2 * Math.PI / 180.0) * Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
        return 2 * R * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }
}
