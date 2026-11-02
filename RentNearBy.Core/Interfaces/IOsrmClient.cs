using RentNearBy.Core.Models;

namespace RentNearBy.Core.Interfaces;

public interface IOsrmClient
{
    bool IsConfigured { get; }
    // Most coordinates one table request may carry.
    int MaxTableCoordinates { get; }
    // null when OSRM failed entirely.
    Task<RoadMatrix?> TableAsync(IReadOnlyList<(double Lat, double Lon)> points, CancellationToken ct = default);
    // sources/destinations index into points; null when the call failed.
    Task<RoadTable?> TableAsync(IReadOnlyList<(double Lat, double Lon)> points, IReadOnlyList<int> sources, IReadOnlyList<int> destinations, CancellationToken ct = default);
    // null when no route.
    Task<RoadRoute?> RouteAsync(IReadOnlyList<(double Lat, double Lon)> points, CancellationToken ct = default);
}
