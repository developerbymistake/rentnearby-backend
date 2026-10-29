using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RentNearBy.Infrastructure.Services;

public interface IRazorpayService
{
    // amount in rupees; receipt doubles as the Idempotency-Key, so retrying the same receipt never makes a second order.
    Task<(string OrderId, int Amount)> CreateOrderAsync(int amount, string receipt, IReadOnlyDictionary<string, string>? notes = null);
    bool VerifyPaymentSignature(string orderId, string paymentId, string signature);
    bool VerifyWebhookSignature(string rawBody, string? signatureHeader);
    string GetKeyId();
    bool IsWebhookConfigured { get; }
    Task<IReadOnlyList<RazorpayPaymentAttempt>> FetchPaymentsForOrderAsync(string orderId);
    // Full or partial refund in paise; receipt is the Idempotency-Key. Returns the refund id.
    Task<string> RefundAsync(string paymentId, long amountPaise, string receipt);
}

// One entry per payment attempt Razorpay recorded against an order (an order can have more than one
// attempt — e.g. a failed try followed by a successful retry). Status is Razorpay's own vocabulary:
// created|authorized|captured|refunded|failed. AmountPaise/Currency are what Razorpay recorded.
public record RazorpayPaymentAttempt(string Id, string Status, long AmountPaise = 0, string Currency = "INR", string? OrderId = null);

public class RazorpayService : IRazorpayService
{
    private readonly string _keyId;
    private readonly string _keySecret;
    private readonly string? _webhookSecret;
    private readonly HttpClient _httpClient;
    private readonly ILogger<RazorpayService> _logger;
    private readonly IAsyncPolicy<HttpResponseMessage> _retryPolicy;
    private const string BaseUrl = "https://api.razorpay.com/v1";
    private const int MaxRetries = 3;
    private const int TimeoutSeconds = 30;

    public RazorpayService(IConfiguration config, HttpClient httpClient, ILogger<RazorpayService> logger)
    {
        _keyId = config["Razorpay:KeyId"]
                 ?? Environment.GetEnvironmentVariable("RAZORPAY_KEY_ID")
                 ?? throw new InvalidOperationException("Razorpay KeyId not configured");
        _keySecret = config["Razorpay:KeySecret"]
                     ?? Environment.GetEnvironmentVariable("RAZORPAY_KEY_SECRET")
                     ?? throw new InvalidOperationException("Razorpay KeySecret not configured");
        // Optional (unlike KeyId/KeySecret): the webhook receiver is a safety net on top of the
        // client-driven checkout flow, not required for checkout itself to function — missing
        // this must not stop the whole service (and every order-creation call) from starting up.
        // VerifyWebhookSignature below rejects everything until this is actually set.
        _webhookSecret = config["Razorpay:WebhookSecret"]
                     ?? Environment.GetEnvironmentVariable("RAZORPAY_WEBHOOK_SECRET");
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

    public async Task<(string OrderId, int Amount)> CreateOrderAsync(int amount, string receipt, IReadOnlyDictionary<string, string>? notes = null)
    {
        if (amount <= 0)
            throw new ArgumentException("Amount must be greater than 0");

        // expire_by was here to bound how long a stale checkout page could still be paid — but
        // this Razorpay account's own settings reject it outright: {"error":{"description":
        // "expire_by is/are not required and should not be sent","reason":"extra_field_sent"}},
        // a hard 400 on every single order-creation call. Confirmed via production logs — not a
        // credentials/account-activation issue, just this field. Removed; orders now use
        // Razorpay's own account-level default expiry instead of one we control per-order.
        var fields = new List<KeyValuePair<string, string>>
        {
            new("amount", ((long)amount * 100).ToString()),
            new("currency", "INR"),
            new("receipt", receipt),
            new("notes[platform]", "RentNearBy"),
        };
        if (notes != null)
            foreach (var (key, value) in notes)
                fields.Add(new($"notes[{key}]", value));

        try
        {
            _logger.LogInformation($"Creating Razorpay order for receipt: {receipt}, amount: {amount}");

            // A fresh message per attempt: a sent HttpRequestMessage cannot be resent, and the header must
            // not go on the shared HttpClient (concurrent requests would see each other's key).
            var response = await _retryPolicy.ExecuteAsync(() =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/orders")
                {
                    Content = new FormUrlEncodedContent(fields),
                };
                request.Headers.TryAddWithoutValidation("Idempotency-Key", receipt);
                return _httpClient.SendAsync(request);
            });

            if (!response.IsSuccessStatusCode)
            {
                // EnsureSuccessStatusCode()'s ex.Message only ever showed the status code (e.g.
                // "401"), never Razorpay's own error body — the one place the actual reason
                // ("key deactivated", "authentication failed", etc.) shows up. Reading it here is
                // the difference between a one-line log fix and a multi-file trace next time this
                // happens. Razorpay's documented error shape: {"error":{"description","reason",...}}.
                var errorBody = await response.Content.ReadAsStringAsync();
                _logger.LogError(
                    "Razorpay order creation failed with status {StatusCode}: {Body}",
                    response.StatusCode, errorBody);

                // 401/403 means the configured key/account itself is the problem — telling the
                // user to "try again" is actively misleading since retrying can't fix that; only
                // 5xx/408/network-ish failures are genuinely transient.
                var message = response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden
                    ? "Payment gateway is not configured correctly. Please contact support."
                    : "Payment gateway temporarily unavailable. Please try again.";
                throw new InvalidOperationException(message);
            }

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
            var computedSignature = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();

            var isValid = CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(computedSignature),
                Encoding.UTF8.GetBytes(signature.ToLowerInvariant()));

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

    // Separate from VerifyPaymentSignature above: webhook signatures are HMAC-SHA256 over the
    // RAW request body bytes (not "orderId|paymentId"), keyed with the webhook secret configured
    // on Razorpay's dashboard (a different value than the checkout KeySecret). Uses a
    // constant-time comparison since this gate sits behind an anonymous, internet-facing route.
    public bool VerifyWebhookSignature(string rawBody, string? signatureHeader)
    {
        if (string.IsNullOrWhiteSpace(_webhookSecret))
        {
            _logger.LogError("Razorpay webhook received but WebhookSecret is not configured — rejecting.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(rawBody) || string.IsNullOrWhiteSpace(signatureHeader))
        {
            _logger.LogWarning("Webhook signature verification attempted with missing body or signature header");
            return false;
        }

        try
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_webhookSecret));
            var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(rawBody));
            var computedSignature = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();

            var isValid = CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(computedSignature),
                Encoding.UTF8.GetBytes(signatureHeader.ToLowerInvariant()));

            if (!isValid)
                _logger.LogWarning("Webhook signature verification failed");

            return isValid;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error verifying webhook signature: {ex.Message}");
            return false;
        }
    }

    public string GetKeyId()
    {
        return _keyId;
    }

    // Safety net over the safety net: the webhook is optional-by-design (see the field comment
    // above), so this lets callers (health check, startup log) surface a missing secret loudly
    // without making it fatal.
    public bool IsWebhookConfigured => !string.IsNullOrWhiteSpace(_webhookSecret);

    // Reconciliation primitive for the plan purchase reconciler and the client verify path: given only
    // the order id of an open purchase, ask Razorpay directly whether any payment attempt against that
    // order ever captured, and for how much. Reuses the same retry+circuit-breaker
    // policy as CreateOrderAsync; failures propagate so the caller can distinguish "confirmed not
    // paid" from "couldn't find out right now".
    public async Task<IReadOnlyList<RazorpayPaymentAttempt>> FetchPaymentsForOrderAsync(string orderId)
    {
        var response = await _retryPolicy.ExecuteAsync(() =>
            _httpClient.GetAsync($"{BaseUrl}/orders/{orderId}/payments"));

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            _logger.LogError(
                "Razorpay fetch-payments-for-order failed for {OrderId} with status {StatusCode}: {Body}",
                orderId, response.StatusCode, errorBody);
            throw new InvalidOperationException($"Razorpay fetch-payments-for-order failed with status {response.StatusCode}");
        }

        var jsonString = await response.Content.ReadAsStringAsync();
        var json = JsonSerializer.Deserialize<JsonElement>(jsonString);

        // A 2xx response whose body doesn't actually have the expected "items" array shape (API
        // version drift, a proxy/CDN error page served as JSON, a truncated-but-parseable body) is
        // NOT the same thing as "this order genuinely has zero payment attempts" — treating it as
        // empty would let the caller conclude NotPaid and abandon a purchase we actually failed to
        // check. Throw here so the caller (ReconcileWithRazorpayAsync) classifies this as Inconclusive
        // instead. An empty `items: []` array (a real, well-formed "zero attempts" answer) is the only
        // case that should legitimately fall through.
        if (!json.TryGetProperty("items", out var itemsEl) || itemsEl.ValueKind != JsonValueKind.Array)
        {
            _logger.LogError(
                "Razorpay fetch-payments-for-order returned an unexpected response shape for {OrderId}: {Body}",
                orderId, jsonString);
            throw new InvalidOperationException("Razorpay fetch-payments-for-order returned an unexpected response shape");
        }

        var attempts = new List<RazorpayPaymentAttempt>();
        foreach (var item in itemsEl.EnumerateArray())
        {
            var id = item.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            var status = item.TryGetProperty("status", out var statusEl) ? statusEl.GetString() : null;
            var amount = item.TryGetProperty("amount", out var amountEl) && amountEl.ValueKind == JsonValueKind.Number ? amountEl.GetInt64() : 0;
            var currency = item.TryGetProperty("currency", out var curEl) ? curEl.GetString() ?? "INR" : "INR";
            var order = item.TryGetProperty("order_id", out var orderEl) ? orderEl.GetString() : null;
            if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(status))
                attempts.Add(new RazorpayPaymentAttempt(id, status, amount, currency, order));
        }
        return attempts;
    }

    public async Task<string> RefundAsync(string paymentId, long amountPaise, string receipt)
    {
        if (amountPaise <= 0) throw new ArgumentException("Refund amount must be greater than 0");

        var fields = new List<KeyValuePair<string, string>>
        {
            new("amount", amountPaise.ToString()),
            new("receipt", receipt),
            new("speed", "normal"),
        };

        var response = await _retryPolicy.ExecuteAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/payments/{paymentId}/refund")
            {
                Content = new FormUrlEncodedContent(fields),
            };
            request.Headers.TryAddWithoutValidation("Idempotency-Key", receipt);
            return _httpClient.SendAsync(request);
        });

        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Razorpay refund failed for payment {PaymentId} with status {StatusCode}: {Body}",
                paymentId, response.StatusCode, body);
            throw new InvalidOperationException($"Razorpay refund failed with status {response.StatusCode}");
        }

        var json = JsonSerializer.Deserialize<JsonElement>(body);
        var refundId = json.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        if (string.IsNullOrEmpty(refundId))
            throw new InvalidOperationException("Razorpay returned an invalid refund id");
        return refundId;
    }
}
