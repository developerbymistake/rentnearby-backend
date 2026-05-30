using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RentNearBy.Core.Interfaces;

namespace RentNearBy.Infrastructure.Services;

public sealed class WhatsAppOtpService : IOtpService
{
    private readonly HttpClient _http;
    private readonly IOtpStore _store;
    private readonly ILogger<WhatsAppOtpService> _logger;
    private readonly string _phoneNumberId;
    private readonly string _accessToken;
    private readonly string _templateName;

    private static readonly TimeSpan OtpTtl = TimeSpan.FromMinutes(10);

    public WhatsAppOtpService(
        HttpClient http,
        IOtpStore store,
        ILogger<WhatsAppOtpService> logger,
        IConfiguration configuration)
    {
        _http = http;
        _store = store;
        _logger = logger;
        _phoneNumberId = configuration["WhatsApp:PhoneNumberId"]
            ?? throw new InvalidOperationException("WhatsApp:PhoneNumberId not configured");
        _accessToken = configuration["WhatsApp:AccessToken"]
            ?? throw new InvalidOperationException("WhatsApp:AccessToken not configured");
        _templateName = configuration["WhatsApp:OtpTemplateName"] ?? "bakhli_otp";
    }

    public async Task<bool> SendOtpAsync(string phoneNumber)
    {
        var otp = GenerateOtp();
        await _store.SaveAsync(phoneNumber, otp, OtpTtl);

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

    public async Task<bool> VerifyOtpAsync(string phoneNumber, string otp)
    {
        var stored = await _store.GetAndDeleteAsync(phoneNumber);
        return stored is not null && stored == otp;
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
}
