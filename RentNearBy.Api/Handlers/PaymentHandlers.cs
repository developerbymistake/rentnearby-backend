using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Services;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

public static class PaymentHandlers
{
    // Server-to-server source of truth for plan purchases, alongside the client verify fast path and the
    // reconciler. Takes HttpContext directly: the signature is only valid over the exact raw body bytes.
    // 200 on every business outcome (duplicate, unknown order, ignored), 500 only on transient failures so
    // Razorpay retries.
    public static async Task<IResult> RazorpayWebhook(
        HttpContext context,
        IPlanPurchaseService purchases,
        IRazorpayService razorpay,
        ILogger<PlanPurchaseService> logger)
    {
        string rawBody;
        using (var reader = new StreamReader(context.Request.Body))
            rawBody = await reader.ReadToEndAsync();

        var signatureHeader = context.Request.Headers["X-Razorpay-Signature"].ToString();
        if (!razorpay.VerifyWebhookSignature(rawBody, signatureHeader))
        {
            logger.LogWarning("Razorpay webhook: signature verification failed");
            return BadRequestResponse("Invalid signature");
        }

        var eventId = context.Request.Headers["X-Razorpay-Event-Id"].ToString();
        try
        {
            var outcome = await purchases.ProcessWebhookAsync(rawBody, string.IsNullOrWhiteSpace(eventId) ? null : eventId);
            if (outcome == WebhookOutcome.Invalid)
            {
                logger.LogWarning("Razorpay webhook: malformed JSON body");
                return BadRequestResponse("Malformed payload");
            }
            return OkResponse(new { acknowledged = true, outcome = outcome.ToString() });
        }
        catch (PlanServiceException ex)
        {
            logger.LogWarning("Razorpay webhook: business outcome {Type} ({Message})", ex.ErrorType, ex.Message);
            return OkResponse(new { acknowledged = true, outcome = ex.ErrorType });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Razorpay webhook: processing failed, asking Razorpay to retry");
            return ServerErrorResponse();
        }
    }

    private const int RtdnMaxBodyBytes = 64 * 1024;

    // Google Play real-time developer notifications via Pub/Sub push (OIDC-authenticated). 200 on every
    // business outcome, 400 for a body that is not a Pub/Sub envelope, 500 only for transient failures so
    // Pub/Sub redelivers.
    public static async Task<IResult> PlayRtdn(
        HttpContext context,
        IPlayRtdnAuthenticator authenticator,
        IPlayBillingSyncService playSync,
        IOptions<PlayBillingOptions> options,
        ILogger<PlayBillingSyncService> logger)
    {
        if (!options.Value.IsConfigured)
            return ErrorResponse(503, "Google Play billing is not configured.", PlanErrorTypes.PlayNotConfigured);

        var auth = await authenticator.ValidateAsync(context.Request.Headers.Authorization.ToString());
        if (!auth.Ok)
        {
            logger.LogWarning(PlaySyncLogEvents.RtdnAuthFailed, "Play RTDN: push authentication failed (email {Email})", auth.Email);
            return UnauthorizedResponse();
        }

        if (context.Request.ContentLength > RtdnMaxBodyBytes)
            return ErrorResponse(413, "Payload too large", "PayloadTooLarge");
        var buffer = new byte[RtdnMaxBodyBytes + 1];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await context.Request.Body.ReadAsync(buffer.AsMemory(read, buffer.Length - read));
            if (n == 0) break;
            read += n;
        }
        if (read > RtdnMaxBodyBytes) return ErrorResponse(413, "Payload too large", "PayloadTooLarge");

        string messageId, dataJson;
        DateTime publishTime;
        try
        {
            var envelope = JsonSerializer.Deserialize<JsonElement>(buffer.AsSpan(0, read));
            if (!envelope.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object
                || !message.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.String
                || !message.TryGetProperty("messageId", out var id) || id.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(id.GetString()))
                return BadRequestResponse("Malformed Pub/Sub envelope");
            messageId = id.GetString()!;
            dataJson = Encoding.UTF8.GetString(Convert.FromBase64String(data.GetString()!));
            publishTime = message.TryGetProperty("publishTime", out var pt) && pt.ValueKind == JsonValueKind.String
                && DateTime.TryParse(pt.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : DateTime.UtcNow;
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            logger.LogWarning("Play RTDN: body is not a Pub/Sub JSON envelope");
            return BadRequestResponse("Malformed payload");
        }

        try
        {
            var outcome = await playSync.ProcessRtdnAsync(messageId, dataJson, publishTime);
            if (outcome == PlayRtdnOutcome.Invalid)
            {
                logger.LogWarning("Play RTDN {MessageId}: message data is not JSON", messageId);
                return BadRequestResponse("Malformed notification");
            }
            return OkResponse(new { acknowledged = true, outcome = outcome.ToString() });
        }
        catch (PlanServiceException ex) when (ex.StatusCode >= 500)
        {
            logger.LogError("Play RTDN {MessageId}: {Type} ({Message}), asking Pub/Sub to retry", messageId, ex.ErrorType, ex.Message);
            return ServerErrorResponse();
        }
        catch (PlanServiceException ex)
        {
            logger.LogWarning("Play RTDN {MessageId}: business outcome {Type} ({Message})", messageId, ex.ErrorType, ex.Message);
            return OkResponse(new { acknowledged = true, outcome = ex.ErrorType });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Play RTDN {MessageId}: processing failed, asking Pub/Sub to retry", messageId);
            return ServerErrorResponse();
        }
    }
}
