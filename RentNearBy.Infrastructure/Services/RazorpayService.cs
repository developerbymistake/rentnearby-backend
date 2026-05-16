using Microsoft.Extensions.Configuration;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RentNearBy.Infrastructure.Services;

public interface IRazorpayService
{
    Task<(string OrderId, int Amount)> CreateOrderAsync(int amount, string receipt);
    bool VerifyPaymentSignature(string orderId, string paymentId, string signature);
}

public class RazorpayService : IRazorpayService
{
    private readonly string _keyId;
    private readonly string _keySecret;
    private readonly HttpClient _httpClient;
    private const string BaseUrl = "https://api.razorpay.com/v1";

    public RazorpayService(IConfiguration config, HttpClient httpClient)
    {
        _keyId = config["Razorpay:KeyId"] ?? throw new InvalidOperationException("Razorpay KeyId not configured");
        _keySecret = config["Razorpay:KeySecret"] ?? throw new InvalidOperationException("Razorpay KeySecret not configured");
        _httpClient = httpClient;
        SetupHttpClient();
    }

    private void SetupHttpClient()
    {
        var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_keyId}:{_keySecret}"));
        _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", auth);
    }

    public async Task<(string OrderId, int Amount)> CreateOrderAsync(int amount, string receipt)
    {
        var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("amount", (amount * 100).ToString()),
            new KeyValuePair<string, string>("currency", "INR"),
            new KeyValuePair<string, string>("receipt", receipt),
            new KeyValuePair<string, string>("notes[platform]", "RentNearBy")
        });

        var response = await _httpClient.PostAsync($"{BaseUrl}/orders", content);
        response.EnsureSuccessStatusCode();

        var jsonString = await response.Content.ReadAsStringAsync();
        var json = JsonSerializer.Deserialize<JsonElement>(jsonString);
        return (json.GetProperty("id").GetString()!, json.GetProperty("amount").GetInt32() / 100);
    }

    public bool VerifyPaymentSignature(string orderId, string paymentId, string signature)
    {
        var text = $"{orderId}|{paymentId}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_keySecret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(text));
        var computedSignature = BitConverter.ToString(hash).Replace("-", "").ToLower();
        return computedSignature == signature.ToLower();
    }
}
