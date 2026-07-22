using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface ICoinPackPurchaseRepository
{
    Task AddAsync(CoinPackPurchase purchase);
    Task<CoinPackPurchase?> GetByIdAsync(Guid id);
    Task<CoinPackPurchase?> GetByRazorpayOrderIdAsync(string orderId);
    Task<IEnumerable<CoinPackPurchase>> GetByUserIdAsync(Guid userId);

    // Atomic UPDATE ... WHERE Status IN ('PENDING','ABANDONED','CANCELLED','FAILED') — a losing
    // concurrent caller (client verify racing the webhook) affects 0 rows and is told so, rather than
    // overwriting a result that already landed. ABANDONED/CANCELLED/FAILED are included (not just
    // PENDING) so a genuine-but-late credit still self-corrects a purchase the cleanup sweep, a
    // user's own cancel-order call, or an earlier failed attempt on the same order already flipped —
    // only SUCCESS is never re-matched/re-flipped.
    Task<bool> MarkSuccessIfPendingAsync(Guid purchaseId, string paymentId, string signature);

    // Atomic UPDATE ... WHERE Status = 'PENDING' — used to supersede a purchase's own stale-pending
    // order (e.g. re-requesting a coin pack after the app-level 20-min reuse window). A tracked-entity
    // mutation + SaveChangesAsync here would blindly overwrite whatever status a concurrent
    // webhook/client-verify/reconciliation-sweep just wrote; this only ever touches a row that is
    // still genuinely Pending at the moment of the write.
    Task<bool> MarkAbandonedIfPendingAsync(Guid purchaseId, string reason);

    // Same atomic-guard shape as the two above, for the user's own /cancel-order call — a losing
    // race against a concurrent webhook/verify-call that just credited this purchase leaves it
    // Success, untouched, instead of being blindly stomped to Cancelled.
    Task<bool> MarkCancelledIfPendingAsync(Guid purchaseId);

    // Same atomic-guard shape, for the Razorpay webhook's payment.failed branch — a losing race
    // against the client's own verify-payment call (which may have already credited and flipped
    // this purchase to Success in the gap) leaves it untouched instead of being stomped to Failed.
    Task<bool> MarkFailedIfPendingOrAbandonedAsync(Guid purchaseId, string paymentId, string failureReason);
}
