using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public enum CaptureOutcome
{
    Granted,
    AlreadyProcessed,
    Blocked,
    NotFound,
}

// Notification is the PlanActivated row written with the grant (already published to notification.push);
// API paths may also push it over SignalR.
public sealed record CaptureResult(CaptureOutcome Outcome, PlanPurchase? Purchase, NotificationEvent? Notification);

public enum WebhookOutcome
{
    Processed,
    Duplicate,
    UnknownOrder,
    Ignored,
    Invalid,
    PartialRefund,
}

public enum ReconcileOutcome
{
    Granted,
    AlreadySettled,
    Refunded,
    NotPaid,
    Inconclusive,
    NotFound,
}

public sealed record AdminGrantResult(AdminPurchaseDto Purchase, NotificationEvent? Notification, bool Replayed);

// Prepaid plan purchases (Razorpay orders), trials, admin grants/revokes/refunds and the coupon engine.
// Every failure is a PlanServiceException (StatusCode + PlanErrorTypes.*). External Razorpay calls never
// run inside a DB transaction; every one-time effect is status-guarded and idempotent.
public interface IPlanPurchaseService
{
    Task<PlanCatalogDto> GetCatalogAsync(string? featureKey);
    Task<MyPlanDto> GetMyPlanAsync(Guid userId);

    Task<QuoteDto> QuoteAsync(Guid userId, QuoteRequest request);
    Task<CreatePlanOrderDto> CreateOrderAsync(Guid userId, CreatePlanOrderRequest request);

    // Client fast path after checkout. AlreadyProcessed returns the current status.
    Task<(PurchaseStatusDto Status, NotificationEvent? Notification)> VerifyAsync(Guid userId, Guid purchaseId, VerifyPaymentRequest request);
    Task<PurchaseStatusDto> GetStatusAsync(Guid userId, Guid purchaseId);
    Task<PurchaseStatusDto?> GetOpenAsync(Guid userId);
    Task<List<PurchaseHistoryItemDto>> GetHistoryAsync(Guid userId, int limit);

    // Shared settle path for verify, webhook, reconciler and admin recheck.
    Task<CaptureResult> ApplyCapturedAsync(Guid purchaseId, string paymentId, long amountPaise, string currency, string source);
    Task<bool> MarkFailedIfOpenAsync(Guid purchaseId, string? paymentId, string reason);
    Task<bool> MarkRefundedAsync(string paymentId, string? refundId);

    // Razorpay webhook body (signature already verified by the caller). Inserts PaymentEvent first.
    Task<WebhookOutcome> ProcessWebhookAsync(string rawBody, string? eventIdHeader);

    // Reconciler / admin recheck: asks Razorpay about one purchase and settles it.
    Task<ReconcileOutcome> ReconcileOrderAsync(Guid purchaseId, string source);
    // Created/Pending past ExpiresAt -> Expired (coupon released). Returns rows expired.
    Task<int> ExpireStaleOpenAsync(int batchSize);

    Task<MyPlanDto> StartTrialAsync(Guid userId, Guid planId);

    Task<AdminGrantResult> AdminGrantAsync(Guid adminId, Guid userId, AdminGrantPlanRequest request);
    Task AdminRevokeAsync(Guid adminId, Guid userId, AdminRevokePlanRequest request);
    Task<AdminPurchaseDto> AdminRefundAsync(Guid adminId, Guid purchaseId, string reason);

    // Role change / correction: cancel the leaving feature's active plans (no refund). Caller's transaction.
    Task<int> CancelActivePlansAsync(Guid userId, string featureKey, string note);
}
