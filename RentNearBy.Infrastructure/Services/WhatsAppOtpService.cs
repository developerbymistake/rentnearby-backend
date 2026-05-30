using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RentNearBy.Core.Interfaces;
using StackExchange.Redis;

namespace RentNearBy.Infrastructure.Services;

public sealed class WhatsAppOtpService : IOtpService
{
    private readonly HttpClient _http;
    private readonly IConnectionMultiplexer? _redis;
    private readonly IMemoryCache _memoryCache;
    private readonly ILogger<WhatsAppOtpService> _logger;
    private readonly string _phoneNumberId;
    private readonly string _accessToken;
    private readonly string _templateName;

    private static readonly TimeSpan OtpTtl = TimeSpan.FromMinutes(10);

    public WhatsAppOtpService(
        HttpClient http,
        IMemoryCache memoryCache,
        ILogger<WhatsAppOtpService> logger,
        IConfiguration configuration,
        IConnectionMultiplexer? redis = null)
    {
        _http = http;
        _memoryCache = memoryCache;
        _redis = redis;
        _logger = logger;
        _phoneNumberId = configuration["WhatsApp:PhoneNumberId"]
            ?? throw new InvalidOperationException("WhatsApp:PhoneNumberId not configured");
        _accessToken = configuration["WhatsApp:AccessToken"]
            ?? throw new InvalidOperationException("WhatsApp:AccessToken not configured");
        _templateName = configuration["WhatsApp:OtpTemplateName"] ?? "bakhli_otp";
    }

    public async Task<bool> SendOtpAsync(string phoneNumber, string keyNamespace = "user")
    {
        var otp = GenerateOtp();
        await StoreOtpAsync(phoneNumber, otp, keyNamespace);

        using var request = BuildRequest(phoneNumber, otp);
        var response = await _http.SendAsync(request);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            _logger.LogError(
                "WhatsApp OTP delivery failed for {PhonePrefix}: HTTP {Status} — {Body}",
                phoneNumber[..4] + "xxxxxx", (int)response.StatusCode, body);
            return false;
        }

        return true;
    }

    public async Task<bool> VerifyOtpAsync(string phoneNumber, string otp, string keyNamespace = "user")
    {
        var stored = await GetOtpAsync(phoneNumber, keyNamespace);
        if (stored is null)
        {
            _logger.LogWarning("VerifyOtp: no OTP found for {PhonePrefix}", phoneNumber[..4] + "xxxxxx");
            return false;
        }
        if (stored != otp)
        {
            _logger.LogWarning("VerifyOtp: OTP mismatch for {PhonePrefix}", phoneNumber[..4] + "xxxxxx");
            return false;
        }

        // Delete only on successful match — wrong attempts keep OTP alive for retry
        await DeleteOtpAsync(phoneNumber, keyNamespace);
        return true;
    }

    private async Task StoreOtpAsync(string phone, string otp, string keyNamespace)
    {
        if (_redis is not null)
        {
            var db = _redis.GetDatabase();
            await db.StringSetAsync(OtpKey(phone, keyNamespace), otp, OtpTtl);
            return;
        }
        _memoryCache.Set(OtpKey(phone, keyNamespace), otp, OtpTtl);
    }

    private async Task<string?> GetOtpAsync(string phone, string keyNamespace)
    {
        if (_redis is not null)
        {
            var db = _redis.GetDatabase();
            var val = await db.StringGetAsync(OtpKey(phone, keyNamespace));
            return val.IsNull ? null : (string?)val;
        }
        _memoryCache.TryGetValue(OtpKey(phone, keyNamespace), out string? val2);
        return val2;
    }

    private async Task DeleteOtpAsync(string phone, string keyNamespace)
    {
        if (_redis is not null)
        {
            var db = _redis.GetDatabase();
            await db.KeyDeleteAsync(OtpKey(phone, keyNamespace));
            return;
        }
        _memoryCache.Remove(OtpKey(phone, keyNamespace));
    }

    private HttpRequestMessage BuildRequest(string phoneNumber, string otp)
    {
        var payload = new
        {
            messaging_product = "whatsapp",
            to = "91" + phoneNumber,
            type = "template",
            template = new
            {
                name = _templateName,
                language = new { code = "en" },
                components = new object[]
                {
                    new
                    {
                        type = "body",
                        parameters = new[] { new { type = "text", text = otp } }
                    },
                    new
                    {
                        type = "button",
                        sub_type = "url",
                        index = "0",
                        parameters = new[] { new { type = "text", text = otp } }
                    }
                }
            }
        };

        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://graph.facebook.com/v20.0/{_phoneNumberId}/messages")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        return request;
    }

    private static string GenerateOtp() =>
        RandomNumberGenerator.GetInt32(1000, 10000).ToString();

    private static string OtpKey(string phone, string keyNamespace) => $"otp:{keyNamespace}:{phone}";
}
