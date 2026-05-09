using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using RentNearBy.Core.Interfaces;

namespace RentNearBy.Infrastructure.Services;

public class NominatimGeocodingService : IGeocodingService
{
    private readonly HttpClient _http;

    public NominatimGeocodingService(HttpClient http) => _http = http;

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

    private record NominatimResult(
        [property: JsonPropertyName("lat")] string Lat,
        [property: JsonPropertyName("lon")] string Lon,
        [property: JsonPropertyName("display_name")] string DisplayName
    );
}
