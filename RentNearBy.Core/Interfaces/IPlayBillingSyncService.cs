using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Models;

namespace RentNearBy.Core.Interfaces;

// The one path that turns a Google Play subscription snapshot into our rows (RTDN, verify-play, restore,
// reconciler, admin). Google is called outside the transaction; every transition is status-guarded.
public interface IPlayBillingSyncService
{
    Task<PlaySyncResult> SyncTokenAsync(PlaySyncRequest request);
    Task<PlayRtdnOutcome> ProcessRtdnAsync(string messageId, string dataJson, DateTime publishTime);
    Task<PlaySyncResult> ApplyVoidedAsync(string purchaseToken, string? orderId, int refundType, string source);
    Task<int> RunPendingServerActionsAsync(int batchSize);
    Task<int> SyncDueAsync(int batchSize);
    Task<int> SyncStaleAsync(int batchSize);
    Task<int> ReconcileVoidedAsync();
    Task<AdminPurchaseDto> AdminRefundPlayAsync(Guid adminId, Guid purchaseId, string reason);
}
