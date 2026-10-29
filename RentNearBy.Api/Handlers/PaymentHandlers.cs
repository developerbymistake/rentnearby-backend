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
}
