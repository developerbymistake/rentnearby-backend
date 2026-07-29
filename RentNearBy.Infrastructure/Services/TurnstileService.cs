using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RentNearBy.Core.Interfaces;

namespace RentNearBy.Infrastructure.Services;

// Server-side verification against Cloudflare's own siteverify API — this is the actual security
// boundary, not the frontend widget. Deliberately FAILS CLOSED (rejects) on any error — missing secret,
// network failure, non-2xx response, malformed body — unlike RedisRateLimitService's "fail open" idiom.
// Rate-limiting is a nice-to-have that degrades gracefully if Redis is down; Turnstile is the one hard
// gate the whole web-enquiry flow leans on, so an unverifiable token must never be treated as valid.
public class TurnstileService : ITurnstileService
{
    private const string VerifyUrl = "https://challenges.cloudflare.com/turnstile/v0/siteverify";

    private readonly HttpClient _httpClient;
    private readonly string? _secretKey;
    private readonly ILogger<TurnstileService> _logger;

    public TurnstileService(HttpClient httpClient, IConfiguration configuration, ILogger<TurnstileService> logger)
    {
        _httpClient = httpClient;
        _secretKey = configuration["Turnstile:SecretKey"];
        _logger = logger;
    }

    public async Task<bool> VerifyAsync(string token, string? remoteIp = null)
    {
        if (string.IsNullOrWhiteSpace(_secretKey))
        {
            _logger.LogError("Turnstile:SecretKey is not configured — failing closed, rejecting every captcha verification");
            return false;
        }
        if (string.IsNullOrWhiteSpace(token)) return false;

        try
        {
            var form = new Dictionary<string, string>
            {
                ["secret"] = _secretKey,
                ["response"] = token,
            };
            if (!string.IsNullOrWhiteSpace(remoteIp)) form["remoteip"] = remoteIp;

            using var response = await _httpClient.PostAsync(VerifyUrl, new FormUrlEncodedContent(form));
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Turnstile siteverify returned {StatusCode}", response.StatusCode);
                return false;
            }

            var result = await response.Content.ReadFromJsonAsync<TurnstileVerifyResponse>();
            return result?.Success == true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Turnstile siteverify request failed — failing closed");
            return false;
        }
    }

    private class TurnstileVerifyResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }
    }
}
