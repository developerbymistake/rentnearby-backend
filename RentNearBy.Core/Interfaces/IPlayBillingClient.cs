using RentNearBy.Core.Models;

namespace RentNearBy.Core.Interfaces;

// Google Play Developer API (androidpublisher v3). Failures surface as PlayApiException.
public interface IPlayBillingClient
{
    bool IsConfigured { get; }
    Task<PlaySubscriptionSnapshot> GetSubscriptionAsync(string purchaseToken, CancellationToken ct = default);
    Task AcknowledgeAsync(string purchaseToken, string productId, CancellationToken ct = default);
    Task RevokeAsync(string purchaseToken, PlayRefundKind kind, CancellationToken ct = default);
    Task CancelAsync(string purchaseToken, PlayCancelKind kind, CancellationToken ct = default);
    Task<IReadOnlyList<PlayVoidedPurchase>> ListVoidedSubscriptionPurchasesAsync(DateTime sinceUtc, CancellationToken ct = default);
}
