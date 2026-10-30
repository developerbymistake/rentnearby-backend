// DI (ServiceCollectionExtensions):
//   services.Configure<PlayBillingOptions>(configuration.GetSection(PlayBillingOptions.Section));
//   services.AddSingleton<IPlayBillingClient, PlayBillingClient>();
using System.Globalization;
using System.Net;
using System.Text.Json;
using Google.Apis.AndroidPublisher.v3;
using Google.Apis.AndroidPublisher.v3.Data;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Requests;
using Google.Apis.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;

namespace RentNearBy.Infrastructure.Services;

// androidpublisher v3. Responses are read as raw JSON (snapshot RawJson keeps every field, including ones the
// generated model may not carry); bodies come from the generated request classes.
public sealed class PlayBillingClient : IPlayBillingClient, IDisposable
{
    private const string Scope = "https://www.googleapis.com/auth/androidpublisher";
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan VoidedWindow = TimeSpan.FromDays(30);

    private readonly PlayBillingOptions _options;
    private readonly ILogger<PlayBillingClient> _logger;
    private readonly Lazy<AndroidPublisherService> _service;

    public PlayBillingClient(IOptions<PlayBillingOptions> options, ILogger<PlayBillingClient> logger)
    {
        _options = options.Value;
        _logger = logger;
        _service = new Lazy<AndroidPublisherService>(Build, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public bool IsConfigured => _options.IsConfigured;

    public static string TokenTail(string? token)
        => string.IsNullOrEmpty(token) ? string.Empty
            : token.Length <= PlayRules.TokenTailLength ? token : token[^PlayRules.TokenTailLength..];

    private AndroidPublisherService Build()
    {
        var credential = !string.IsNullOrWhiteSpace(_options.ServiceAccountJson)
            ? GoogleCredential.FromJson(_options.ServiceAccountJson)
            : GoogleCredential.FromFile(_options.ServiceAccountJsonPath!);
        var service = new AndroidPublisherService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential.CreateScoped(Scope),
            ApplicationName = "RentNearBy",
        });
        service.HttpClient.Timeout = CallTimeout;
        return service;
    }

    private AndroidPublisherService Service
    {
        get
        {
            if (!_options.IsConfigured)
                throw new PlayApiException(PlayApiErrorKind.PermissionDenied, "Play billing is not configured.");
            try
            {
                return _service.Value;
            }
            catch (Exception ex) when (ex is not PlayApiException)
            {
                _logger.LogError(new EventId(0, "play.api.error"), ex, "Play client could not be built (op {Op})", "init");
                throw new PlayApiException(PlayApiErrorKind.PermissionDenied, "Play service account is invalid.", ex);
            }
        }
    }

    // ------------------------------------------------------------------ calls

    public async Task<PlaySubscriptionSnapshot> GetSubscriptionAsync(string purchaseToken, CancellationToken ct = default)
    {
        var service = Service;
        string json;
        try
        {
            json = await SendAsync(() => service.Purchases.Subscriptionsv2.Get(_options.PackageName, purchaseToken), "get", purchaseToken, ct);
        }
        catch (PlayApiException ex) when (ex.Kind == PlayApiErrorKind.Transient && !ct.IsCancellationRequested)
        {
            json = await SendAsync(() => service.Purchases.Subscriptionsv2.Get(_options.PackageName, purchaseToken), "get", purchaseToken, ct);
        }

        try
        {
            return Parse(purchaseToken, json);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            _logger.LogError(new EventId(0, "play.api.error"), ex, "Play get returned an unreadable body (op {Op}, token {TokenTail})", "get", TokenTail(purchaseToken));
            throw new PlayApiException(PlayApiErrorKind.Invalid, "Unreadable subscription resource.", ex);
        }
    }

    public async Task AcknowledgeAsync(string purchaseToken, string productId, CancellationToken ct = default)
    {
        var service = Service;
        await SendAsync(() => service.Purchases.Subscriptions.Acknowledge(
            new SubscriptionPurchasesAcknowledgeRequest(), _options.PackageName, productId, purchaseToken), "acknowledge", purchaseToken, ct);
    }

    public async Task RevokeAsync(string purchaseToken, PlayRefundKind kind, CancellationToken ct = default)
    {
        var service = Service;
        var context = kind == PlayRefundKind.Full
            ? new RevocationContext { FullRefund = new RevocationContextFullRefund() }
            : new RevocationContext { ProratedRefund = new RevocationContextProratedRefund() };
        await SendAsync(() => service.Purchases.Subscriptionsv2.Revoke(
            new RevokeSubscriptionPurchaseRequest { RevocationContext = context }, _options.PackageName, purchaseToken), "revoke", purchaseToken, ct);
    }

    public async Task CancelAsync(string purchaseToken, PlayCancelKind kind, CancellationToken ct = default)
    {
        var service = Service;
        var type = kind == PlayCancelKind.UserRequestedStopRenewals ? "USER_REQUESTED_STOP_RENEWALS" : "DEVELOPER_REQUESTED_STOP_PAYMENTS";
        await SendAsync(() => service.Purchases.Subscriptionsv2.Cancel(
            new CancelSubscriptionPurchaseRequest { CancellationContext = new CancellationContext { CancellationType = type } },
            _options.PackageName, purchaseToken), "cancel", purchaseToken, ct);
    }

    public async Task<IReadOnlyList<PlayVoidedPurchase>> ListVoidedSubscriptionPurchasesAsync(DateTime sinceUtc, CancellationToken ct = default)
    {
        var service = Service;
        var floor = DateTime.UtcNow - VoidedWindow + TimeSpan.FromMinutes(5);
        var since = sinceUtc < floor ? floor : sinceUtc;
        var startMillis = new DateTimeOffset(DateTime.SpecifyKind(since, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

        var result = new List<PlayVoidedPurchase>();
        string? pageToken = null;
        do
        {
            var token = pageToken;
            var json = await SendAsync(() =>
            {
                var request = service.Purchases.Voidedpurchases.List(_options.PackageName);
                request.Type = 1;
                request.StartTime = startMillis;
                request.MaxResults = 1000;
                if (token != null) request.Token = token;
                return request;
            }, "voided.list", null, ct);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("voidedPurchases", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var v in items.EnumerateArray())
                {
                    var purchaseToken = Str(v, "purchaseToken");
                    var orderId = Str(v, "orderId");
                    if (purchaseToken == null || orderId == null) continue;
                    var voidedMillis = Long(v, "voidedTimeMillis");
                    result.Add(new PlayVoidedPurchase(
                        purchaseToken,
                        orderId,
                        voidedMillis is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime : DateTime.UtcNow,
                        (int)(Long(v, "voidedSource") ?? 0),
                        (int)(Long(v, "voidedReason") ?? 0)));
                }
            }
            pageToken = root.TryGetProperty("tokenPagination", out var pagination) ? Str(pagination, "nextPageToken") : null;
        }
        while (!string.IsNullOrEmpty(pageToken));

        return result;
    }

    // One HTTP exchange through the authorised client; maps every failure onto PlayApiException.
    private async Task<string> SendAsync<T>(Func<ClientServiceRequest<T>> build, string op, string? purchaseToken, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CallTimeout);
        try
        {
            using var request = build().CreateRequest();
            using var response = await _service.Value.HttpClient.SendAsync(request, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            if (response.IsSuccessStatusCode) return body;

            var status = (int)response.StatusCode;
            var kind = KindOf(response.StatusCode);
            var level = kind == PlayApiErrorKind.Transient ? LogLevel.Warning : LogLevel.Error;
            _logger.Log(level, new EventId(0, "play.api.error"),
                "Play {Op} failed: HTTP {Status} {Kind} (token {TokenTail}) {Reason}",
                op, status, kind, TokenTail(purchaseToken), ErrorMessageOf(body));
            throw new PlayApiException(kind, $"Google Play {op} failed with HTTP {status}.");
        }
        catch (PlayApiException)
        {
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogWarning(new EventId(0, "play.api.error"), "Play {Op} timed out (token {TokenTail})", op, TokenTail(purchaseToken));
            throw new PlayApiException(PlayApiErrorKind.Transient, $"Google Play {op} timed out.", ex);
        }
        catch (TokenResponseException ex)
        {
            _logger.LogError(new EventId(0, "play.api.error"), ex, "Play {Op}: service account token refused", op);
            throw new PlayApiException(PlayApiErrorKind.PermissionDenied, "Play service account was refused.", ex);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(new EventId(0, "play.api.error"), ex, "Play {Op} network failure (token {TokenTail})", op, TokenTail(purchaseToken));
            throw new PlayApiException(PlayApiErrorKind.Transient, $"Google Play {op} is unreachable.", ex);
        }
    }

    private static PlayApiErrorKind KindOf(HttpStatusCode status) => (int)status switch
    {
        404 or 410 => PlayApiErrorKind.NotFound,
        401 or 403 => PlayApiErrorKind.PermissionDenied,
        408 or 429 => PlayApiErrorKind.Transient,
        >= 500 => PlayApiErrorKind.Transient,
        _ => PlayApiErrorKind.Invalid,
    };

    private static string? ErrorMessageOf(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error) && Str(error, "message") is { } message)
                return message.Length > 200 ? message[..200] : message;
        }
        catch (JsonException)
        {
        }
        return null;
    }

    // ------------------------------------------------------------------ snapshot

    internal static PlaySubscriptionSnapshot Parse(string purchaseToken, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var lineItems = new List<PlaySubscriptionLineItem>();
        if (root.TryGetProperty("lineItems", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var li in items.EnumerateArray())
            {
                var productId = Str(li, "productId");
                if (string.IsNullOrEmpty(productId)) continue;

                bool? autoRenew = null;
                long? priceMicros = null;
                string? currency = null, changeMode = null, changeState = null;
                if (Obj(li, "autoRenewingPlan") is { } plan)
                {
                    autoRenew = Bool(plan, "autoRenewEnabled");
                    if (Obj(plan, "recurringPrice") is { } money)
                    {
                        currency = Str(money, "currencyCode");
                        priceMicros = (Long(money, "units") ?? 0) * 1_000_000 + (Long(money, "nanos") ?? 0) / 1_000;
                    }
                    if (Obj(plan, "priceChangeDetails") is { } change)
                    {
                        changeMode = Str(change, "priceChangeMode");
                        changeState = Str(change, "priceChangeState");
                    }
                }
                var offer = Obj(li, "offerDetails");
                lineItems.Add(new PlaySubscriptionLineItem(
                    productId,
                    Time(li, "expiryTime"),
                    offer is { } o1 ? Str(o1, "basePlanId") : null,
                    offer is { } o2 ? Str(o2, "offerId") : null,
                    autoRenew,
                    Obj(li, "prepaidPlan") != null,
                    Str(li, "latestSuccessfulOrderId"),
                    Obj(li, "deferredItemReplacement") is { } d ? Str(d, "productId") : null,
                    priceMicros,
                    currency,
                    changeMode,
                    changeState));
            }
        }

        // Legacy top-level order id only when a single item leaves no ambiguity.
        if (lineItems is [{ LatestOrderId: null } only] && Str(root, "latestOrderId") is { } legacyOrder)
            lineItems = [only with { LatestOrderId = legacyOrder }];

        string? cancelReason = null;
        if (Obj(root, "canceledStateContext") is { } cancelled)
        {
            foreach (var key in new[] { PlayCancelReasons.Replacement, PlayCancelReasons.Developer, PlayCancelReasons.System, PlayCancelReasons.User })
            {
                if (cancelled.TryGetProperty(key, out var member) && member.ValueKind != JsonValueKind.Null)
                {
                    cancelReason = key;
                    break;
                }
            }
        }

        var external = Obj(root, "externalAccountIdentifiers");
        return new PlaySubscriptionSnapshot(
            purchaseToken,
            Str(root, "subscriptionState") ?? PlayStates.Unspecified,
            Str(root, "acknowledgementState") ?? PlayAckStates.Pending,
            Time(root, "startTime"),
            Str(root, "linkedPurchaseToken"),
            Obj(root, "outOfAppPurchaseContext") is { } outOfApp ? Str(outOfApp, "expiredPurchaseToken") : null,
            Str(root, "regionCode"),
            Obj(root, "testPurchase") != null,
            external is { } e1 ? Str(e1, "obfuscatedExternalAccountId") : null,
            external is { } e2 ? Str(e2, "obfuscatedExternalProfileId") : null,
            cancelReason,
            Obj(root, "pausedStateContext") is { } paused ? Time(paused, "autoResumeTime") : null,
            lineItems,
            json);
    }

    private static JsonElement? Obj(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    private static string? Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool? Bool(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean() : null;

    // int64 arrives as a JSON string (proto3 mapping) or a number.
    private static long? Long(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s)) return s;
        return null;
    }

    private static DateTime? Time(JsonElement e, string name)
        => Str(e, name) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t)
            ? t.UtcDateTime : null;

    public void Dispose()
    {
        if (_service.IsValueCreated) _service.Value.Dispose();
    }
}
