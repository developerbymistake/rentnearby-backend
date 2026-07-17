using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface ICoinPackPurchaseRepository
{
    Task AddAsync(CoinPackPurchase purchase);
    Task<CoinPackPurchase?> GetByIdAsync(Guid id);
    Task<CoinPackPurchase?> GetByRazorpayOrderIdAsync(string orderId);
    Task<IEnumerable<CoinPackPurchase>> GetByUserIdAsync(Guid userId);

    // Atomic UPDATE ... WHERE Status = 'PENDING' — a losing concurrent caller (client verify racing
    // the webhook) affects 0 rows and is told so, rather than overwriting a result that already landed.
    Task<bool> MarkSuccessIfPendingAsync(Guid purchaseId, string paymentId, string signature);
}
