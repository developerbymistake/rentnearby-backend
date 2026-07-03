using NetTopologySuite.Geometries;

namespace RentNearBy.Core.Interfaces;

public record GeoPoint(decimal Latitude, decimal Longitude, string? DisplayName = null);

public interface IGeocodingService
{
    Task<GeoPoint?> GeocodeAsync(string query);
    Task<Geometry?> FetchDistrictBoundaryAsync(string districtName, string stateName);
}
