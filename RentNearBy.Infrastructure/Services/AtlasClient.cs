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

    public async Task<AtlasCitySpots?> GetCitySpotsAsync(long cityKey, CancellationToken ct = default)
    {
        var (status, body) = await GetAsync($"api/v1/cities/{cityKey}/spots", ct);
        if (status == HttpStatusCode.NotFound) return null;
        if (status != HttpStatusCode.OK)
            throw new AtlasUnavailableException($"Atlas city spots answered {(int)status}");
        return ParseData<AtlasCitySpots>(body);
    }

    public async Task<AtlasCityPage> SearchCitiesAsync(string? q, int page, int pageSize, bool readyOnly = false,
        bool popularOnly = false, Guid? stateId = null, CancellationToken ct = default)
    {
        var path = $"api/v1/cities?page={page}&pageSize={pageSize}&ready={(readyOnly ? "true" : "false")}";
        if (popularOnly) path += "&popular=true";
        if (stateId.HasValue) path += "&stateId=" + stateId.Value.ToString("D");
        if (!string.IsNullOrWhiteSpace(q)) path += "&q=" + Uri.EscapeDataString(q);
        var (status, body) = await GetAsync(path, ct);
        if (status != HttpStatusCode.OK)
            throw new AtlasUnavailableException($"Atlas cities answered {(int)status}");
        return ParseData<AtlasCityPage>(body);
    }

    public async Task<AtlasCatalogVersion> GetCatalogVersionAsync(CancellationToken ct = default)
    {
        var (status, body) = await GetAsync("api/v1/cities/version", ct);
        if (status != HttpStatusCode.OK)
            throw new AtlasUnavailableException($"Atlas cities version answered {(int)status}");
        return ParseData<AtlasCatalogVersion>(body);
    }

    public async Task<List<TripMapPackInfo>> GetMapPacksAsync(CancellationToken ct = default)
    {
        var (status, body) = await GetAsync("api/v1/map-packs", ct);
        if (status != HttpStatusCode.OK)
            throw new AtlasUnavailableException($"Atlas map packs answered {(int)status}");
        return ParseMapPacks(body);
    }

    public async Task<byte[]?> GetMapPackRawAsync(Guid stateId, CancellationToken ct = default)
    {
        var (status, body) = await GetAsync($"api/v1/map-packs/{stateId:D}", ct);
        if (status == HttpStatusCode.NotFound) return null;
        if (status != HttpStatusCode.OK)
            throw new AtlasUnavailableException($"Atlas map pack answered {(int)status}");
        return ParseRawData(body);
    }

    internal static List<TripMapPackInfo> ParseMapPacks(string body)
    {
        var raw = ParseRawData(body);
        var result = new List<TripMapPackInfo>();
        using var doc = JsonDocument.Parse(raw);
        if (!doc.RootElement.TryGetProperty("packs", out var packs) || packs.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var item in packs.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (!item.TryGetProperty("stateId", out var idEl) || idEl.ValueKind != JsonValueKind.String
                || !Guid.TryParse(idEl.GetString(), out var stateId)) continue;
            if (!item.TryGetProperty("version", out var vEl) || vEl.ValueKind != JsonValueKind.String) continue;
            var version = vEl.GetString();
            if (!TripMapPackKeys.IsVersion(version)) continue;

            var name = item.TryGetProperty("stateName", out var nEl) && nEl.ValueKind == JsonValueKind.String
                ? nEl.GetString() ?? string.Empty : string.Empty;
            var count = item.TryGetProperty("count", out var cEl) && cEl.ValueKind == JsonValueKind.Number
                && cEl.TryGetInt32(out var c) && c >= 0 ? c : 0;
            var bbox = new List<double>(4);
            if (item.TryGetProperty("bbox", out var bEl) && bEl.ValueKind == JsonValueKind.Array && bEl.GetArrayLength() == 4)
                foreach (var n in bEl.EnumerateArray())
                    if (n.ValueKind == JsonValueKind.Number && n.TryGetDouble(out var d) && double.IsFinite(d)) bbox.Add(d);

            result.Add(new TripMapPackInfo
            {
                StateId = stateId,
                StateName = name,
                Version = version!,
                Count = count,
                Bbox = bbox.Count == 4 ? bbox.ToArray() : [],
            });
        }
        return result;
    }

    // Raw text of the envelope's data object, unchanged, as UTF-8.
    internal static byte[] ParseRawData(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Object)
                throw new AtlasUnavailableException("Atlas response has no data");
            return System.Text.Encoding.UTF8.GetBytes(data.GetRawText());
        }
        catch (JsonException)
        {
            throw new AtlasUnavailableException("Atlas response is not valid JSON");
        }
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
