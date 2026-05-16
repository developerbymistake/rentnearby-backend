using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
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
    private readonly ILogger<RazorpayService> _logger;
    private readonly IAsyncPolicy<HttpResponseMessage> _retryPolicy;
    private const string BaseUrl = "https://api.razorpay.com/v1";
    private const int MaxRetries = 3;
    private const int TimeoutSeconds = 30;

    public RazorpayService(IConfiguration config, HttpClient httpClient, ILogger<RazorpayService> logger)
    {
        _keyId = config["Razorpay:KeyId"] ?? throw new InvalidOperationException("Razorpay KeyId not configured");
        _keySecret = config["Razorpay:KeySecret"] ?? throw new InvalidOperationException("Razorpay KeySecret not configured");
        _httpClient = httpClient;
        _logger = logger;
        
        SetupHttpClient();
        _retryPolicy = CreateRetryPolicy();
    }

    private void SetupHttpClient()
    {
        _httpClient.Timeout = TimeSpan.FromSeconds(TimeoutSeconds);
        var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_keyId}:{_keySecret}"));
        _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", auth);
    }

    private IAsyncPolicy<HttpResponseMessage> CreateRetryPolicy()
    {
        var retryPolicy = Policy
            .Handle<HttpRequestException>()
            .Or<OperationCanceledException>()
            .OrResult<HttpResponseMessage>(r =>
                (int)r.StatusCode >= 500 ||  // Server errors
                r.StatusCode == System.Net.HttpStatusCode.RequestTimeout)  // Timeout
            .WaitAndRetryAsync(
                retryCount: MaxRetries,
                sleepDurationProvider: attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)),  // Exponential backoff
                onRetry: (outcome, duration, attempt, context) =>
                {
                    _logger.LogWarning($"Razorpay request failed. Retry {attempt}/{MaxRetries} after {duration.TotalSeconds}s. Error: {outcome.Exception?.Message}");
                });

        var circuitBreakerPolicy = Policy
            .Handle<HttpRequestException>()
            .Or<OperationCanceledException>()
            .OrResult<HttpResponseMessage>(r =>
                (int)r.StatusCode >= 500 ||
                r.StatusCode == System.Net.HttpStatusCode.RequestTimeout)
            .CircuitBreakerAsync(
                handledEventsAllowedBeforeBreaking: 5,
                durationOfBreak: TimeSpan.FromMinutes(1));

        return retryPolicy.WrapAsync(circuitBreakerPolicy);
    }

    public async Task<(string OrderId, int Amount)> CreateOrderAsync(int amount, string receipt)
    {
        if (amount <= 0)
            throw new ArgumentException("Amount must be greater than 0");

        // Idempotency key: prevents duplicate orders if request is retried
        var idempotencyKey = $"{receipt}-{DateTime.UtcNow:yyyyMMdd}";

        var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("amount", (amount * 100).ToString()),
            new KeyValuePair<string, string>("currency", "INR"),
            new KeyValuePair<string, string>("receipt", receipt),
            new KeyValuePair<string, string>("notes[platform]", "RentNearBy")
        });

        _httpClient.DefaultRequestHeaders.Add("Idempotency-Key", idempotencyKey);

        try
        {
            _logger.LogInformation($"Creating Razorpay order for receipt: {receipt}, amount: {amount}");

            var response = await _retryPolicy.ExecuteAsync(() => 
                _httpClient.PostAsync($"{BaseUrl}/orders", content));

            response.EnsureSuccessStatusCode();

            var jsonString = await response.Content.ReadAsStringAsync();
            var json = JsonSerializer.Deserialize<JsonElement>(jsonString);
            
            var orderId = json.GetProperty("id").GetString();
            var returnedAmount = json.GetProperty("amount").GetInt32() / 100;

            if (string.IsNullOrEmpty(orderId))
                throw new InvalidOperationException("Razorpay returned invalid order ID");

            _logger.LogInformation($"Order created successfully: {orderId}");
            return (orderId, returnedAmount);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError($"Razorpay API failed after {MaxRetries} retries: {ex.Message}");
            throw new InvalidOperationException("Payment gateway temporarily unavailable. Please try again.", ex);
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogError($"Razorpay request timeout: {ex.Message}");
            throw new InvalidOperationException("Payment gateway request timed out. Please try again.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Unexpected error creating Razorpay order: {ex.Message}");
            throw;
        }
        finally
        {
            _httpClient.DefaultRequestHeaders.Remove("Idempotency-Key");
        }
    }

    public bool VerifyPaymentSignature(string orderId, string paymentId, string signature)
    {
        if (string.IsNullOrWhiteSpace(orderId) || 
            string.IsNullOrWhiteSpace(paymentId) || 
            string.IsNullOrWhiteSpace(signature))
        {
            _logger.LogWarning("Signature verification attempted with missing parameters");
            return false;
        }

        try
        {
            var text = $"{orderId}|{paymentId}";
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_keySecret));
            var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(text));
            var computedSignature = BitConverter.ToString(hash).Replace("-", "").ToLower();
            
            var isValid = computedSignature == signature.ToLower();
            
            if (!isValid)
            {
                _logger.LogWarning($"Signature verification failed for order: {orderId}");
            }

            return isValid;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error verifying signature: {ex.Message}");
            return false;
        }
    }
}
