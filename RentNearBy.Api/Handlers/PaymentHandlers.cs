using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using RentNearBy.Api.Hubs;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Services;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

public static class PaymentHandlers
{
    // Server-to-server safety net alongside the client-driven /verify-payment flow on
    // CoinPackHandlers.VerifyPayment — if the app dies between Razorpay showing success and the
    // client finishing its own verify call, this is the only thing that ever credits the coins.
    // Deliberately takes HttpContext directly (no bound request DTO): Razorpay's signature is only
    // valid over the exact raw body bytes it sent, and minimal-API JSON model binding would
    // consume/transform that stream before we could verify it.
    public static async Task<IResult> RazorpayWebhook(
        HttpContext context,
        IUnitOfWork unitOfWork,
        ICoinPackPurchaseService purchaseService,
        IRazorpayService razorpay,
        ILogger<CoinPackPurchaseService> logger,
        IHubContext<WalletHub> hubContext)
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

        JsonElement root;
        try
        {
            root = JsonSerializer.Deserialize<JsonElement>(rawBody);
        }
        catch (JsonException)
        {
            logger.LogWarning("Razorpay webhook: malformed JSON body");
            return BadRequestResponse("Malformed payload");
        }

        var eventType = root.TryGetProperty("event", out var eventEl) ? eventEl.GetString() : null;
        if (eventType != "payment.captured" && eventType != "payment.failed")
            return OkResponse(new { acknowledged = true }); // not an event we act on yet

        if (!root.TryGetProperty("payload", out var payloadEl) ||
            !payloadEl.TryGetProperty("payment", out var paymentEl) ||
            !paymentEl.TryGetProperty("entity", out var entityEl))
        {
            logger.LogWarning($"Razorpay webhook: {eventType} payload missing expected fields");
            return BadRequestResponse("Malformed payload");
        }

        var orderId = entityEl.TryGetProperty("order_id", out var orderIdEl) ? orderIdEl.GetString() : null;
        var paymentId = entityEl.TryGetProperty("id", out var paymentIdEl) ? paymentIdEl.GetString() : null;

        if (string.IsNullOrWhiteSpace(orderId) || string.IsNullOrWhiteSpace(paymentId))
        {
            logger.LogWarning($"Razorpay webhook: {eventType} payload missing order_id/payment id");
            return BadRequestResponse("Malformed payload");
        }

        var purchase = await unitOfWork.CoinPackPurchases.GetByRazorpayOrderIdAsync(orderId);
        if (purchase == null)
        {
            logger.LogWarning($"Razorpay webhook: no coin pack purchase found for order {orderId}");
            return OkResponse(new { acknowledged = true }); // nothing to reconcile against
        }

        if (eventType == "payment.failed")
        {
            // Only record the failure if nothing else has already settled this purchase — same
            // "PENDING and ABANDONED are both still-open outcomes" reasoning the old membership
            // webhook used for PaymentTransaction.
            if (purchase.Status == CoinPackPurchaseStatuses.Pending || purchase.Status == CoinPackPurchaseStatuses.Abandoned)
            {
                var errorDescription = entityEl.TryGetProperty("error_description", out var errEl)
                    ? errEl.GetString() : null;
                purchase.Status = CoinPackPurchaseStatuses.Failed;
                purchase.FailureReason = string.IsNullOrWhiteSpace(errorDescription)
                    ? "Payment failed at Razorpay" : errorDescription;
                purchase.RazorpayPaymentId = paymentId;
                purchase.CompletedAt = DateTime.UtcNow;
                await unitOfWork.SaveChangesAsync();
                logger.LogInformation($"Razorpay webhook: coin pack purchase {purchase.Id} marked FAILED ({purchase.FailureReason})");
            }
            return OkResponse(new { acknowledged = true });
        }

        // From here on, eventType == "payment.captured"
        if (purchase.Status == CoinPackPurchaseStatuses.Success)
        {
            // The normal case — client's own /verify-payment already handled it.
            //
            // Deliberately NOT also terminal here: FAILED (a late authorisation after an apparent
            // failure, particularly with UPI, per Razorpay's own docs) or ABANDONED (this purchase
            // timed out in PendingCoinPurchaseCleanupService before we heard back) — both must still
            // fall through and credit below. CreditCoinsAsync is itself idempotent, so a late
            // duplicate success is harmless either way.
            return OkResponse(new { acknowledged = true });
        }

        var verifyRequest = new VerifyPaymentRequest
        {
            RazorpayOrderId = orderId,
            RazorpayPaymentId = paymentId,
            RazorpaySignature = string.Empty, // unused — skipSignatureCheck bypasses the client-signature gate
        };

        try
        {
            var response = await purchaseService.VerifyAndCreditAsync(purchase.UserId, verifyRequest, skipSignatureCheck: true);
            logger.LogInformation($"Razorpay webhook: credited coin pack purchase {purchase.Id} for order {orderId}");

            // This is the highest-value push site in the whole feature: the webhook only ever fires
            // this path when the client's own /verify-payment call never completed (app crashed
            // mid-payment), so a push here is the ONLY way the device finds out coins landed without
            // the user manually reopening the wallet screen. No ClaimsPrincipal exists on this
            // anonymous, HMAC-authenticated endpoint — target purchase.UserId directly. Best-effort:
            // never let a push failure affect this webhook's own response code (Razorpay retries
            // non-2xx responses, and the credit itself already succeeded above).
            try
            {
                await hubContext.Clients.Group($"user_{purchase.UserId}").SendAsync("WalletBalanceChanged", new
                {
                    balance = response.NewBalance,
                    reason = CoinTransactionReasons.Recharge,
                    occurredAt = DateTime.UtcNow,
                });
            }
            catch { }

            return OkResponse(new { acknowledged = true });
        }
        catch (InvalidOperationException ex)
        {
            // e.g. "Already processed" if the client's own call raced this one and won — benign.
            logger.LogInformation($"Razorpay webhook: coin pack purchase {purchase.Id} no-op ({ex.Message})");
            return OkResponse(new { acknowledged = true });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Razorpay webhook: failed to credit coin pack purchase {purchase.Id}");
            return ServerErrorResponse(); // non-2xx — let Razorpay's own retry mechanism try again later
        }
    }
}
