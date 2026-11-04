using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public sealed record TaxiRouteGeometryWork(
    Guid RouteId, double FromLat, double FromLng, double ToLat, double ToLng, string EndsHash, TaxiRouteGeometry? Existing);

public interface ITaxiRouteGeometryRepository
{
    // Routes of public taxis that need a (new) road line, oldest first.
    Task<IReadOnlyList<TaxiRouteGeometryWork>> GetDueAsync(int take, DateTime failedBeforeUtc);

    // State name of the active district containing the point, null when none.
    Task<string?> FindContainingStateAsync(double lat, double lng);

    Task UpsertAsync(TaxiRouteGeometry row);
}
