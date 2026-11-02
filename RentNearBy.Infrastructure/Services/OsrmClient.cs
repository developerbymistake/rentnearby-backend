using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;

namespace RentNearBy.Infrastructure.Services;

public class OsrmClient : IOsrmClient
{
    private readonly HttpClient _http;
    private readonly ILogger<OsrmClient> _logger;
    private readonly string _profile;
    public bool IsConfigured { get; }
    public int MaxTableCoordinates { get; }

    // Registered as a plain typed client, so timeout and base address are applied here.
    public OsrmClient(HttpClient http, IOptions<OsrmOptions> options, ILogger<OsrmClient> logger)
    {
        _http = http;
        _logger = logger;

        var o = options.Value;
        _http.Timeout = TimeSpan.FromSeconds(o.TimeoutSeconds > 0 ? o.TimeoutSeconds : 20);
        _profile = Uri.EscapeDataString(string.IsNullOrWhiteSpace(o.Profile) ? "driving" : o.Profile.Trim());
        MaxTableCoordinates = o.MaxTableCoordinates >= 2 ? o.MaxTableCoordinates : 100;

        if (!o.IsConfigured) return;
        if (!Uri.TryCreate(o.BaseUrl!.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var baseUri))
        {
            _logger.LogError("OSRM BaseUrl is not a valid absolute URL; road times fall back to estimates");
            return;
        }

        _http.BaseAddress = baseUri;
        IsConfigured = true;
    }

    public async Task<RoadMatrix?> TableAsync(IReadOnlyList<(double Lat, double Lon)> points, CancellationToken ct = default)
    {
        if (!IsConfigured || points.Count < 2 || points.Count > MaxTableCoordinates) return null;
        var body = await GetAsync($"table/v1/{_profile}/{Coordinates(points)}?annotations=duration,distance", ct);
        return body == null ? null : ParseTable(body, points.Count);
    }

    public async Task<RoadTable?> TableAsync(IReadOnlyList<(double Lat, double Lon)> points, IReadOnlyList<int> sources,
        IReadOnlyList<int> destinations, CancellationToken ct = default)
    {
        if (!IsConfigured || points.Count < 2 || points.Count > MaxTableCoordinates
            || sources.Count == 0 || destinations.Count == 0
            || sources.Concat(destinations).Any(i => i < 0 || i >= points.Count))
            return null;
        var body = await GetAsync($"table/v1/{_profile}/{Coordinates(points)}?sources={string.Join(';', sources)}"
            + $"&destinations={string.Join(';', destinations)}&annotations=duration,distance", ct);
        return body == null ? null : ParseTableBlock(body, sources.Count, destinations.Count);
    }

    public async Task<RoadRoute?> RouteAsync(IReadOnlyList<(double Lat, double Lon)> points, CancellationToken ct = default)
    {
        if (!IsConfigured || points.Count < 2) return null;
        var body = await GetAsync($"route/v1/{_profile}/{Coordinates(points)}?overview=full&geometries=geojson&steps=false", ct);
        return body == null ? null : ParseRoute(body);
    }

    private async Task<string?> GetAsync(string path, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(path, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            // OSRM answers 400 with a JSON code (NoRoute, InvalidQuery); the parser decides.
            if (!response.IsSuccessStatusCode && (int)response.StatusCode != 400)
            {
                _logger.LogWarning("OSRM answered {Status}", (int)response.StatusCode);
                return null;
            }
            return body;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning("OSRM call failed: {Error}", ex.Message);
            return null;
        }
    }

    // "lon,lat;lon,lat" in invariant culture; only numbers ever reach the URL.
    internal static string Coordinates(IReadOnlyList<(double Lat, double Lon)> points)
        => string.Join(';', points.Select(p =>
            $"{p.Lon.ToString("0.######", CultureInfo.InvariantCulture)},{p.Lat.ToString("0.######", CultureInfo.InvariantCulture)}"));

    // durations in seconds, distances in metres; a null or missing cell becomes an estimate.
    internal static RoadMatrix? ParseTable(string body, int n)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!IsOk(root)) return null;
            if (!root.TryGetProperty("durations", out var durations) || durations.ValueKind != JsonValueKind.Array
                || durations.GetArrayLength() != n)
                return null;
            var hasDistances = root.TryGetProperty("distances", out var distances)
                && distances.ValueKind == JsonValueKind.Array && distances.GetArrayLength() == n;

            var minutes = new int[n, n];
            var km = new double[n, n];
            var isEstimate = new bool[n, n];
            for (var i = 0; i < n; i++)
            {
                var row = durations[i];
                var distRow = hasDistances ? distances[i] : default;
                for (var j = 0; j < n; j++)
                {
                    if (i == j) continue;
                    var sec = Cell(row, j, n);
                    var metres = hasDistances ? Cell(distRow, j, n) : null;
                    if (sec == null || metres == null)
                    {
                        isEstimate[i, j] = true;
                        continue;
                    }
                    minutes[i, j] = RoadUnits.Minutes(sec.Value);
                    km[i, j] = RoadUnits.Km(metres.Value);
                }
            }
            return new RoadMatrix(minutes, km, isEstimate);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    // rows = sources, cols = destinations; a missing distances array leaves every metre cell null.
    internal static RoadTable? ParseTableBlock(string body, int rows, int cols)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!IsOk(root)) return null;
            if (!root.TryGetProperty("durations", out var durations) || durations.ValueKind != JsonValueKind.Array
                || durations.GetArrayLength() != rows)
                return null;
            var hasDistances = root.TryGetProperty("distances", out var distances)
                && distances.ValueKind == JsonValueKind.Array && distances.GetArrayLength() == rows;

            var seconds = new double?[rows, cols];
            var metres = new double?[rows, cols];
            for (var i = 0; i < rows; i++)
            {
                var row = durations[i];
                var distRow = hasDistances ? distances[i] : default;
                for (var j = 0; j < cols; j++)
                {
                    seconds[i, j] = Cell(row, j, cols);
                    metres[i, j] = hasDistances ? Cell(distRow, j, cols) : null;
                }
            }
            return new RoadTable(seconds, metres);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    internal static RoadRoute? ParseRoute(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!IsOk(root)) return null;
            if (!root.TryGetProperty("routes", out var routes) || routes.ValueKind != JsonValueKind.Array
                || routes.GetArrayLength() == 0)
                return null;

            var route = routes[0];
            if (!route.TryGetProperty("geometry", out var geometry) || geometry.ValueKind != JsonValueKind.Object) return null;
            var sec = route.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : 0;
            var metres = route.TryGetProperty("distance", out var m) && m.ValueKind == JsonValueKind.Number ? m.GetDouble() : 0;
            return new RoadRoute((int)Math.Ceiling(sec / 60.0), Math.Round(metres / 1000.0, 1), geometry.Clone());
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static bool IsOk(JsonElement root)
        => root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("code", out var code)
            && code.ValueKind == JsonValueKind.String
            && code.GetString() == "Ok";

    private static double? Cell(JsonElement row, int j, int n)
    {
        if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() != n) return null;
        var cell = row[j];
        return cell.ValueKind == JsonValueKind.Number ? cell.GetDouble() : null;
    }
}
