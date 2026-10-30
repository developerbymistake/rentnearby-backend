using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface IPlanPurchaseRepository
{
    Task AddAsync(PlanPurchase purchase);
    Task<PlanPurchase?> GetByIdAsync(Guid id);
    Task<PlanPurchase?> GetByOrderIdAsync(string razorpayOrderId);
    Task<PlanPurchase?> GetByPaymentIdAsync(string razorpayPaymentId);
    Task<PlanPurchase?> GetByIdempotencyKeyAsync(string key);
    Task<PlanPurchase?> GetByPlayOrderIdAsync(string playOrderId);
    Task<List<PlanPurchase>> GetByPlayOrderIdsAsync(IReadOnlyCollection<string> playOrderIds);
    // Latest Granted charge row of a Play subscription (admin refund target).
    Task<PlanPurchase?> GetLatestGrantedBySubscriptionAsync(Guid playSubscriptionId);

    // One query on ix_planpurchases_one_open.
    Task<PlanPurchase?> GetOpenByUserFeatureAsync(Guid userId, string featureKey);
    Task<PlanPurchase?> GetLatestOpenByUserAsync(Guid userId);
    // The open Play order of the same plan+cycle (reused as the first charge row).
    Task<PlanPurchase?> GetOpenPlayByUserFeatureAsync(Guid userId, string featureKey, Guid planId, short cycleMonths);

    Task<List<PlanPurchase>> GetHistoryAsync(Guid userId, int limit);

    // Admin keyset list, newest first ((CreatedAt DESC, Id) index).
    Task<List<PlanPurchase>> GetAdminPageAsync(string? status, Guid? userId, DateTime? afterCreatedAt, Guid? afterId, int limit);

    // Reconciler scan on (Status, CreatedAt), bounded.
    Task<List<(Guid Id, DateTime CreatedAt)>> ListIdsForReconcileAsync(
        string[] statuses, DateTime createdBefore, DateTime? createdAfter, int limit, bool newestFirst = false,
        string? providerNot = null);

    // Status-guarded transitions (single UPDATE, never read-then-write).
    Task<bool> SetOrderIdIfCreatedAsync(Guid id, string razorpayOrderId);
    Task<bool> MarkPendingIfCreatedAsync(Guid id);
}
