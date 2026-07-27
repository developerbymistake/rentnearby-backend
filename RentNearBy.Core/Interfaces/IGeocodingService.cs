using NetTopologySuite.Geometries;

namespace RentNearBy.Core.Interfaces;

public record GeoPoint(decimal Latitude, decimal Longitude, string? DisplayName = null);

public record ReverseGeocodeResult(string DisplayName, string? PlaceName);

public interface IGeocodingService
{
    Task<GeoPoint?> GeocodeAsync(string query);
    Task<Geometry?> FetchDistrictBoundaryAsync(string districtName, string stateName);
    Task<ReverseGeocodeResult?> ReverseGeocodeAsync(double lat, double lng);
}
