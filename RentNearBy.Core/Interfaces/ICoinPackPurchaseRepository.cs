using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface ICoinPackPurchaseRepository
{
    Task AddAsync(CoinPackPurchase purchase);
    Task<CoinPackPurchase?> GetByIdAsync(Guid id);
    Task<CoinPackPurchase?> GetByRazorpayOrderIdAsync(string orderId);
    Task<IEnumerable<CoinPackPurchase>> GetByUserIdAsync(Guid userId);

    // Atomic UPDATE ... WHERE Status IN ('PENDING','ABANDONED','CANCELLED') — a losing concurrent
    // caller (client verify racing the webhook) affects 0 rows and is told so, rather than
    // overwriting a result that already landed. ABANDONED/CANCELLED are included (not just PENDING)
    // so a genuine-but-late credit still self-corrects a purchase the cleanup sweep or a user's own
    // cancel-order call already flipped — SUCCESS/FAILED are never matched/re-flipped.
    Task<bool> MarkSuccessIfPendingAsync(Guid purchaseId, string paymentId, string signature);
}
