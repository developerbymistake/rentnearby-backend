using System.Text.Json;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;

namespace RentNearBy.Tests.Support.Buddy;

// Route geometry only: a LineString over the asked points, or null for every route while Down (OSRM unreachable).
public sealed class FakeRoadCache : ITripRoadCache
{
    public bool Down { get; set; }
    public List<IReadOnlyList<(double Lat, double Lon)>> Asked { get; } = [];

    public Task<CityRoadMatrix?> GetCityMatrixAsync(long cityKey, CancellationToken ct = default) => Task.FromResult<CityRoadMatrix?>(null);
    public Task<CityRoadMatrix?> PeekCityMatrixAsync(long cityKey, CancellationToken ct = default) => Task.FromResult<CityRoadMatrix?>(null);
    public Task<CityRoadMatrix?> ReadCityMatrixAsync(long cityKey, CancellationToken ct = default) => Task.FromResult<CityRoadMatrix?>(null);
    public Task InvalidateAsync(long cityKey) => Task.CompletedTask;

    public async Task<JsonElement?> GetRouteGeometryAsync(IReadOnlyList<(double Lat, double Lon)> points, CancellationToken ct = default)
        => (await GetRouteGeometriesAsync([points], ct))[0];

    public Task<JsonElement?[]> GetRouteGeometriesAsync(IReadOnlyList<IReadOnlyList<(double Lat, double Lon)>> routes, CancellationToken ct = default)
    {
        Asked.AddRange(routes);
        return Task.FromResult(routes.Select(r => Down ? null : (JsonElement?)Line(r)).ToArray());
    }

    public static JsonElement Line(IReadOnlyList<(double Lat, double Lon)> points)
        => JsonDocument.Parse(JsonSerializer.Serialize(new { type = "LineString", coordinates = points.Select(p => new[] { p.Lon, p.Lat }) })).RootElement.Clone();
}
