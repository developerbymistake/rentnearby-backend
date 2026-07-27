using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using RentNearBy.Core.Interfaces;

namespace RentNearBy.Infrastructure.Services;

public class NominatimGeocodingService : IGeocodingService
{
    private readonly HttpClient _http;
    private static readonly WKTReader WktReader = new();

    public NominatimGeocodingService(HttpClient http) => _http = http;

    // Used for city geocoding — returns centroid lat/lng
    public async Task<GeoPoint?> GeocodeAsync(string query)
    {
        var url = $"search?q={Uri.EscapeDataString(query)}&format=json&limit=1&countrycodes=in";
        try
        {
            var results = await _http.GetFromJsonAsync<NominatimResult[]>(url);
            var first = results?.FirstOrDefault();
            if (first is null) return null;

            if (!decimal.TryParse(first.Lat, NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)) return null;
            if (!decimal.TryParse(first.Lon, NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)) return null;

            return new GeoPoint(Math.Round(lat, 6), Math.Round(lon, 6), first.DisplayName);
        }
        catch
        {
            return null;
        }
    }

    // Used for district creation — returns full WKT boundary polygon via polygon_text=1
    public async Task<Geometry?> FetchDistrictBoundaryAsync(string districtName, string stateName)
    {
        var query = Uri.EscapeDataString($"{districtName}, {stateName}, India");
        var url = $"search?q={query}&format=json&limit=1&countrycodes=in&polygon_text=1";
        try
        {
            var results = await _http.GetFromJsonAsync<NominatimDistrictResult[]>(url);
            var first = results?.FirstOrDefault();
            if (first is null || string.IsNullOrWhiteSpace(first.GeoText)) return null;
            if (!first.GeoText.StartsWith("POLYGON") && !first.GeoText.StartsWith("MULTIPOLYGON")) return null;

            var geometry = WktReader.Read(first.GeoText);
            geometry.SRID = 4326;
            return geometry;
        }
        catch
        {
            return null;
        }
    }

    // Used for address/city resolution at listing creation — returns display address + best-effort place name
    public async Task<ReverseGeocodeResult?> ReverseGeocodeAsync(double lat, double lng)
    {
        var url = $"reverse?lat={lat.ToString(CultureInfo.InvariantCulture)}&lon={lng.ToString(CultureInfo.InvariantCulture)}&format=jsonv2";
        try
        {
            var result = await _http.GetFromJsonAsync<NominatimReverseResult>(url);
            if (result is null || string.IsNullOrWhiteSpace(result.DisplayName)) return null;
            var placeName = result.Address?.City ?? result.Address?.Town ?? result.Address?.Municipality
                ?? result.Address?.Village ?? result.Address?.Suburb ?? result.Address?.County;
            return new ReverseGeocodeResult(result.DisplayName, placeName?.Trim());
        }
        catch
        {
            return null;
        }
    }

    private record NominatimResult(
        [property: JsonPropertyName("lat")] string Lat,
        [property: JsonPropertyName("lon")] string Lon,
        [property: JsonPropertyName("display_name")] string DisplayName
    );

    private record NominatimDistrictResult(
        [property: JsonPropertyName("geotext")] string? GeoText
    );

    private record NominatimReverseResult(
        [property: JsonPropertyName("display_name")] string? DisplayName,
        [property: JsonPropertyName("address")] NominatimAddress? Address
    );

    private record NominatimAddress(
        [property: JsonPropertyName("city")] string? City,
        [property: JsonPropertyName("town")] string? Town,
        [property: JsonPropertyName("municipality")] string? Municipality,
        [property: JsonPropertyName("village")] string? Village,
        [property: JsonPropertyName("suburb")] string? Suburb,
        [property: JsonPropertyName("county")] string? County
    );
}
