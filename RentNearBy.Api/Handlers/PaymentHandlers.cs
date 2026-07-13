using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Services;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

public static class PaymentHandlers
{
    public record CancelOrderRequest(string RazorpayOrderId);

    public static async Task<IResult> CancelOrder(
        CancelOrderRequest request,
        ClaimsPrincipal principal,
        IUnitOfWork unitOfWork)
    {
        if (string.IsNullOrWhiteSpace(request.RazorpayOrderId))
            return BadRequestResponse("RazorpayOrderId is required");

        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var tx = (await unitOfWork.PaymentTransactions.GetByUserIdAsync(userId))
            .FirstOrDefault(t => t.RazorpayOrderId == request.RazorpayOrderId
                              && t.Status == "PENDING");

        if (tx == null)
            return OkResponse(new { cancelled = false }); // idempotent — already cancelled or not found

        tx.Status = "CANCELLED";
        await unitOfWork.SaveChangesAsync();
        return OkResponse(new { cancelled = true });
    }


    public static async Task<IResult> CreateOrder(
        Guid listingId,
        [FromBody] PaymentPlanRequest request,
        ClaimsPrincipal principal,
        IPaymentService paymentService)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.PlanType))
            return BadRequestResponse("RoomPlan type is required");

        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        try
        {
            var response = await paymentService.CreateOrderAsync(userId, listingId, request.PlanType.Trim().ToUpperInvariant());
            return OkResponse(response);
        }
        catch (ArgumentException)
        {
            return BadRequestResponse("Invalid payment parameters");
        }
        catch (KeyNotFoundException)
        {
            return NotFoundResponse("RoomListing or plan not found");
        }
        catch (UnauthorizedAccessException)
        {
            return UnauthorizedResponse("You don't have permission to create order for this listing");
        }
        catch (InvalidOperationException ex)
        {
            return BadRequestResponse(ex.Message);
        }
        catch (Exception)
        {
            return ServerErrorResponse();
        }
    }

    public static async Task<IResult> InitiatePayment(
        Guid listingId,
        [FromBody] PaymentPlanRequest request,
        ClaimsPrincipal principal,
        IPaymentService paymentService)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.PlanType))
            return BadRequestResponse("RoomPlan type is required");

        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        try
        {
            var response = await paymentService.InitiatePaymentAsync(userId, listingId, request.PlanType.Trim().ToUpperInvariant());
            return OkResponse(response);
        }
        catch (ArgumentException)
        {
            return BadRequestResponse("Invalid payment parameters");
        }
        catch (KeyNotFoundException)
        {
            return NotFoundResponse("RoomListing not found");
        }
        catch (UnauthorizedAccessException)
        {
            return UnauthorizedResponse("You don't have permission to initiate payment for this listing");
        }
        catch (InvalidOperationException ex)
        {
            return BadRequestResponse(ex.Message);
        }
        catch (Exception)
        {
            return ServerErrorResponse();
        }
    }

    public static async Task<IResult> VerifyPayment(
        Guid listingId,
        VerifyPaymentRequest request,
        ClaimsPrincipal principal,
        IPaymentService paymentService)
    {
        if (string.IsNullOrWhiteSpace(request.RazorpayOrderId) ||
            string.IsNullOrWhiteSpace(request.RazorpayPaymentId) ||
            string.IsNullOrWhiteSpace(request.RazorpaySignature))
            return BadRequestResponse("Missing payment verification details");

        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        try
        {
            var response = await paymentService.VerifyAndActivateAsync(userId, request);
            return OkResponse(response);
        }
        catch (KeyNotFoundException)
        {
            return NotFoundResponse("Transaction not found or listing not found");
        }
        catch (UnauthorizedAccessException)
        {
            return UnauthorizedResponse("You don't have permission to verify this payment");
        }
        catch (InvalidOperationException ex)
        {
            return BadRequestResponse(ex.Message);
        }
        catch (Exception)
        {
            return ServerErrorResponse();
        }
    }

    public static async Task<IResult> GetMembershipStatus(
        ClaimsPrincipal principal,
        IPaymentService paymentService,
        IUnitOfWork unitOfWork)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        try
        {
            var membership = await unitOfWork.RoomMemberships.GetActiveByUserIdAsync(userId);
            var activeRooms = await paymentService.GetActiveRoomCountAsync(userId);
            var canActivate = await paymentService.CanUserActivateListingAsync(userId);

            return OkResponse(new
            {
                hasMembership = membership != null,
                planType = membership?.PlanType,
                validUntil = membership?.ValidUntil,
                maxRooms = membership?.MaxRooms ?? 0,
                activeRooms,
                canActivate
            });
        }
        catch (Exception)
        {
            return ServerErrorResponse();
        }
    }

    public static async Task<IResult> CreateUpgradeOrder(
        [FromBody] PaymentPlanRequest request,
        ClaimsPrincipal principal,
        IPaymentService paymentService)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.PlanType))
            return BadRequestResponse("RoomPlan type is required");

        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        try
        {
            var response = await paymentService.CreateUpgradeOrderAsync(userId, request.PlanType.Trim().ToUpperInvariant());
            return OkResponse(response);
        }
        catch (ArgumentException ex) { return BadRequestResponse(ex.Message); }
        catch (InvalidOperationException ex) { return BadRequestResponse(ex.Message); }
        catch (KeyNotFoundException ex) { return NotFoundResponse(ex.Message); }
        catch (Exception) { return ServerErrorResponse(); }
    }

    public static async Task<IResult> VerifyUpgradePayment(
        [FromBody] VerifyPaymentRequest request,
        ClaimsPrincipal principal,
        IPaymentService paymentService)
    {
        if (string.IsNullOrWhiteSpace(request.RazorpayOrderId) ||
            string.IsNullOrWhiteSpace(request.RazorpayPaymentId) ||
            string.IsNullOrWhiteSpace(request.RazorpaySignature))
            return BadRequestResponse("Missing payment verification details");

        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        try
        {
            var response = await paymentService.VerifyUpgradePaymentAsync(userId, request);
            return OkResponse(response);
        }
        catch (KeyNotFoundException) { return NotFoundResponse("Transaction not found"); }
        catch (UnauthorizedAccessException) { return UnauthorizedResponse(); }
        catch (InvalidOperationException ex) { return BadRequestResponse(ex.Message); }
        catch (Exception) { return ServerErrorResponse(); }
    }

    // Server-to-server safety net alongside the client-driven /verify-payment flow above — if
    // the app dies between Razorpay showing success and the client finishing its own verify
    // call, this is the only thing that ever activates the plan. Deliberately takes HttpContext
    // directly (no bound request DTO): Razorpay's signature is only valid over the exact raw
    // body bytes it sent, and minimal-API JSON model binding would consume/transform that
    // stream before we could verify it.
    public static async Task<IResult> RazorpayWebhook(
        HttpContext context,
        IUnitOfWork unitOfWork,
        IPaymentService paymentService,
        IRazorpayService razorpay,
        ILogger<PaymentService> logger)
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

        var transaction = await unitOfWork.PaymentTransactions.GetByRazorpayOrderIdAsync(orderId);
        if (transaction == null)
        {
            logger.LogWarning($"Razorpay webhook: no transaction found for order {orderId}");
            return OkResponse(new { acknowledged = true }); // nothing to reconcile against
        }

        if (eventType == "payment.failed")
        {
            // Only record the failure if nothing else has already settled this transaction —
            // e.g. the client's own signature check may have already marked it FAILED, or (in a
            // race) the payment may have genuinely gone on to succeed by the time this arrives.
            if (transaction.Status == "PENDING")
            {
                var errorDescription = entityEl.TryGetProperty("error_description", out var errEl)
                    ? errEl.GetString() : null;
                transaction.Status = "FAILED";
                transaction.FailureReason = string.IsNullOrWhiteSpace(errorDescription)
                    ? "Payment failed at Razorpay" : errorDescription;
                transaction.RazorpayPaymentId = paymentId;
                transaction.CompletedAt = DateTime.UtcNow;
                await unitOfWork.SaveChangesAsync();
                logger.LogInformation($"Razorpay webhook: transaction {transaction.Id} marked FAILED ({transaction.FailureReason})");
            }
            return OkResponse(new { acknowledged = true });
        }

        // From here on, eventType == "payment.captured"
        if (transaction.Status != "PENDING")
        {
            // SUCCESS = the normal case, client's own /verify-payment already handled it.
            // FAILED = already recorded (client-side signature check, or the payment.failed
            // branch above on a prior webhook delivery). Either way, nothing further to do.
            return OkResponse(new { acknowledged = true });
        }

        if (transaction.UserId == null)
        {
            logger.LogError($"Razorpay webhook: transaction {transaction.Id} has no UserId, cannot activate");
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
            // Unambiguous per PaymentTransaction's own construction sites: TransactionKind is
            // only ever "PLOT" for plot transactions; RoomListingId/PlotId being set (vs. null,
            // for upgrade orders) distinguishes a fresh purchase from an upgrade within each.
            if (transaction.TransactionKind == "PLOT")
            {
                if (transaction.PlotId.HasValue)
                    await paymentService.VerifyPlotListingPaymentAsync(transaction.UserId.Value, verifyRequest, skipSignatureCheck: true);
                else
                    await paymentService.VerifyPlotListingUpgradePaymentAsync(transaction.UserId.Value, verifyRequest, skipSignatureCheck: true);
            }
            else
            {
                if (transaction.RoomListingId.HasValue)
                    await paymentService.VerifyAndActivateAsync(transaction.UserId.Value, verifyRequest, skipSignatureCheck: true);
                else
                    await paymentService.VerifyUpgradePaymentAsync(transaction.UserId.Value, verifyRequest, skipSignatureCheck: true);
            }

            logger.LogInformation($"Razorpay webhook: activated transaction {transaction.Id} for order {orderId}");
            return OkResponse(new { acknowledged = true });
        }
        catch (InvalidOperationException ex)
        {
            // e.g. "Transaction already processed" if the client's own call raced this one and
            // won — benign, not an error.
            logger.LogInformation($"Razorpay webhook: transaction {transaction.Id} no-op ({ex.Message})");
            return OkResponse(new { acknowledged = true });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Razorpay webhook: failed to activate transaction {transaction.Id}");
            return ServerErrorResponse(); // non-2xx — let Razorpay's own retry mechanism try again later
        }
    }
}
