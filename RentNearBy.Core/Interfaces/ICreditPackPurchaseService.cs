using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public enum CreditPackReconcileOutcome { Credited, NotPaid, Inconclusive }

// CreditResponse is only populated when this specific call performed the credit — if another path
// (webhook / client verify) already won the race, Outcome is still Credited but CreditResponse is
// null, telling the caller there is no new balance to push.
public record CreditPackReconcileResult(CreditPackReconcileOutcome Outcome, CreditPackPurchaseVerifyResponse? CreditResponse = null);

public interface ICreditPackPurchaseService
{
    // confirmed=true skips the recent-purchase soft-warning check (RECENT_PURCHASE_DETECTED) — set
    // when the client is re-submitting after the user acknowledged that warning.
    Task<CreatePaymentOrderResponse> CreateOrderAsync(Guid userId, Guid creditPackId, bool confirmed = false);
    Task<CreditPackPurchaseVerifyResponse> VerifyAndCreditAsync(Guid userId, VerifyPaymentRequest request, bool skipSignatureCheck = false);

    // Asks Razorpay directly whether a stuck-Pending purchase's order ever actually captured a
    // payment, and credits it if so. Used by PendingCreditPurchaseCleanupService as a safety net for
    // when the client crashed mid-payment AND the webhook never arrived.
    Task<CreditPackReconcileResult> ReconcileWithRazorpayAsync(CreditPackPurchase purchase);

    // Last-resort client-facing status check — lets the app ask "what happened to my most recent
    // purchase" using only the JWT (no client-held order id required), for the case where the app was
    // killed mid-payment and lost any in-memory order/payment/signature state.
    Task<LatestCreditPackPurchaseResponse> GetLatestPurchaseAsync(Guid userId);
}
