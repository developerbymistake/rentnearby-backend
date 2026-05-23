using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using RentNearBy.Core.Interfaces;

namespace RentNearBy.Infrastructure.Services;

public class OverpassService : IOverpassService
{
    private readonly HttpClient _http;

    private static readonly string[] Endpoints =
    [
        "https://overpass-api.de/api/interpreter",
        "https://overpass.kumi.systems/api/interpreter",
        "https://overpass.openstreetmap.ru/api/interpreter",
    ];

    public OverpassService(HttpClient http) => _http = http;

    public async Task<List<(string Name, double Lat, double Lng)>> FetchCitiesAsync(string districtName, string stateName)
    {
        // Attempt order:
        // 1. admin_level=5 WITH state scope  — correct for most Indian districts, prevents same-name collision
        // 2. admin_level=5 plain             — fallback if OSM state name doesn't match GADM exactly
        // 3. admin_level=6 plain             — fallback for states that tag districts at level 6
        var attempts = new[]
        {
            BuildQuery(districtName, "5", stateName),
            BuildQuery(districtName, "5", null),
            BuildQuery(districtName, "6", null),
        };

        foreach (var query in attempts)
        {
            var result = await TryFetchAsync(query);
            if (result != null && result.Count > 0)
                return result;
        }
        return [];
    }

    private static string BuildQuery(string districtName, string adminLevel, string? stateName)
    {
        var escapedDistrict = districtName.Replace("\"", "\\\"");

        if (!string.IsNullOrWhiteSpace(stateName))
        {
            var escapedState = stateName.Replace("\"", "\\\"");
            return $"""
                [out:json][timeout:60];
                area["boundary"="administrative"]["name"="{escapedState}"]["admin_level"="4"]->.state;
                area(area.state)["boundary"="administrative"]["name"="{escapedDistrict}"]["admin_level"="{adminLevel}"]->.d;
                (
                  node(area.d)["place"~"city|town"];
                  way(area.d)["place"~"city|town"];
                );
                out center;
                """;
        }

        return $"""
            [out:json][timeout:60];
            area["boundary"="administrative"]["name"="{escapedDistrict}"]["admin_level"="{adminLevel}"]->.d;
            (
              node(area.d)["place"~"city|town"];
              way(area.d)["place"~"city|town"];
            );
            out center;
            """;
    }

    private async Task<List<(string, double, double)>?> TryFetchAsync(string query)
    {
        foreach (var endpoint in Endpoints)
        {
            try
            {
                var content = new FormUrlEncodedContent([new("data", query)]);
                using var response = await _http.PostAsync(endpoint, content);
                if (!response.IsSuccessStatusCode) continue;

                var json = await response.Content.ReadFromJsonAsync<OverpassResponse>();
                if (json?.Elements == null) continue;

                var results = new List<(string, double, double)>();
                foreach (var el in json.Elements)
                {
                    var name = el.Tags?.Name;
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    double lat, lng;
                    if (el.Type == "node")
                    {
                        lat = el.Lat ?? 0;
                        lng = el.Lon ?? 0;
                    }
                    else
                    {
                        lat = el.Center?.Lat ?? 0;
                        lng = el.Center?.Lon ?? 0;
                    }
                    if (lat == 0 && lng == 0) continue;

                    results.Add((name, lat, lng));
                }
                return results;
            }
            catch
            {
                // try next endpoint
            }
        }
        return null;
    }

    private sealed class OverpassResponse
    {
        [JsonPropertyName("elements")]
        public List<OverpassElement>? Elements { get; set; }
    }

    private sealed class OverpassElement
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "";

        [JsonPropertyName("lat")]
        public double? Lat { get; set; }

        [JsonPropertyName("lon")]
        public double? Lon { get; set; }

        [JsonPropertyName("center")]
        public OverpassCenter? Center { get; set; }

        [JsonPropertyName("tags")]
        public OverpassTags? Tags { get; set; }
    }

    private sealed class OverpassCenter
    {
        [JsonPropertyName("lat")]
        public double Lat { get; set; }

        [JsonPropertyName("lon")]
        public double Lon { get; set; }
    }

    private sealed class OverpassTags
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }
}
