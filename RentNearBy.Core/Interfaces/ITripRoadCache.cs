using System.Text.Json;
using RentNearBy.Core.Models;

namespace RentNearBy.Core.Interfaces;

public interface ITripRoadCache
{
    // Road times between every spot of the city; null = not cached and could not be built. Waits for the build.
    Task<CityRoadMatrix?> GetCityMatrixAsync(long cityKey, CancellationToken ct = default);
    // Cached matrix or null at once; a miss starts the build in the background (single-flight).
    Task<CityRoadMatrix?> PeekCityMatrixAsync(long cityKey, CancellationToken ct = default);
    Task InvalidateAsync(long cityKey);
    // Read-through over IOsrmClient.RouteAsync keyed by the ordered stops; null = no route.
    Task<JsonElement?> GetRouteGeometryAsync(IReadOnlyList<(double Lat, double Lon)> points, CancellationToken ct = default);
    // Same per route, cache reads in parallel and OSRM misses with bounded concurrency; result[i] belongs to routes[i].
    Task<JsonElement?[]> GetRouteGeometriesAsync(IReadOnlyList<IReadOnlyList<(double Lat, double Lon)>> routes, CancellationToken ct = default);
}
