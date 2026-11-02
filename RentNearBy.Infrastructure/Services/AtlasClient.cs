using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;

namespace RentNearBy.Infrastructure.Services;

public class AtlasClient : IAtlasClient
{
    private const string ApiKeyHeader = "X-Atlas-Api-Key";

    private readonly HttpClient _http;
    private readonly ILogger<AtlasClient> _logger;

    public bool IsConfigured { get; }

    // Registered as a plain typed client, so timeout, base address and key are applied here.
    public AtlasClient(HttpClient http, IOptions<AtlasOptions> options, ILogger<AtlasClient> logger)
    {
        _http = http;
        _logger = logger;

        var o = options.Value;
        _http.Timeout = TimeSpan.FromSeconds(o.TimeoutSeconds > 0 ? o.TimeoutSeconds : 15);

        if (!o.IsConfigured) return;
        if (!Uri.TryCreate(o.BaseUrl!.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var baseUri))
        {
            _logger.LogError("Atlas BaseUrl is not a valid absolute URL; Atlas calls are disabled");
            return;
        }

        _http.BaseAddress = baseUri;
        _http.DefaultRequestHeaders.Add(ApiKeyHeader, o.ApiKey!.Trim());
        IsConfigured = true;
    }

    public async Task<AtlasExportPage> ExportCitiesAsync(long after, int limit, CancellationToken ct = default)
    {
        var (status, body) = await GetAsync($"api/v1/export/cities?after={after}&limit={limit}", ct);
        if (status != HttpStatusCode.OK)
            throw new AtlasUnavailableException($"Atlas export answered {(int)status}");
        return ParseData<AtlasExportPage>(body);
    }

    public async Task<AtlasCitySpots?> GetCitySpotsAsync(long cityKey, CancellationToken ct = default)
    {
        var (status, body) = await GetAsync($"api/v1/cities/{cityKey}/spots", ct);
        if (status == HttpStatusCode.NotFound) return null;
        if (status != HttpStatusCode.OK)
            throw new AtlasUnavailableException($"Atlas city spots answered {(int)status}");
        return ParseData<AtlasCitySpots>(body);
    }

    private async Task<(HttpStatusCode Status, string Body)> GetAsync(string path, CancellationToken ct)
    {
        if (!IsConfigured) throw new AtlasUnavailableException("Atlas is not configured");

        try
        {
            using var response = await _http.GetAsync(path, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotFound)
                _logger.LogWarning("Atlas GET {Path} answered {Status}", path, (int)response.StatusCode);
            return (response.StatusCode, body);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning("Atlas GET {Path} failed: {Error}", path, ex.Message);
            throw new AtlasUnavailableException("Atlas is not reachable");
        }
    }

    // Atlas envelope: { status, code, timestamp, data, error }.
    internal static T ParseData<T>(string body) where T : class
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Object)
                throw new AtlasUnavailableException("Atlas response has no data");

            return data.Deserialize<T>(TripJson.Options)
                ?? throw new AtlasUnavailableException("Atlas response has no data");
        }
        catch (JsonException)
        {
            throw new AtlasUnavailableException("Atlas response is not valid JSON");
        }
    }
}
