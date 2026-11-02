using System.Text.Json;
using RentNearBy.Core.Models;

namespace RentNearBy.Core.Interfaces;

public interface ITripRoadCache
{
    // Road times between every spot of the city; null = not cached and could not be built.
    Task<CityRoadMatrix?> GetCityMatrixAsync(long cityKey, CancellationToken ct = default);
    Task InvalidateAsync(long cityKey);
    // Read-through over IOsrmClient.RouteAsync keyed by the ordered stops; null = no route.
    Task<JsonElement?> GetRouteGeometryAsync(IReadOnlyList<(double Lat, double Lon)> points, CancellationToken ct = default);
}
