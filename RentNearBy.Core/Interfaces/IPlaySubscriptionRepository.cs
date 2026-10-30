using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface IPlaySubscriptionRepository
{
    Task<PlaySubscription?> GetByTokenAsync(string purchaseToken);
    Task<PlaySubscription?> GetByIdAsync(Guid id);
    Task<List<PlaySubscription>> GetByIdsAsync(IReadOnlyCollection<Guid> ids);
    Task<List<PlaySubscription>> GetByUserAsync(Guid userId, int limit);
    Task<PlaySubscription?> GetMappedByUserFeatureAsync(Guid userId, string featureKey);
    Task AddAsync(PlaySubscription row);
    // SELECT 1 ... FOR UPDATE; caller holds the transaction and the owner lock.
    Task LockByTokenAsync(string purchaseToken);
    // Conditional on AcknowledgedAt IS NULL.
    Task<bool> MarkAcknowledgedAsync(Guid id, DateTime at);
    Task<List<Guid>> ListPendingActionsAsync(DateTime now, int limit);
    Task<List<Guid>> ListDueAsync(DateTime before, int limit);
    Task<List<Guid>> ListAckPendingAsync(int limit);
    Task<List<Guid>> ListStaleAsync(DateTime syncedBefore, int limit);
    // /plans/me.play rule (2): latest Mapped subscription in a hold/paused/grace or recently ended state.
    Task<PlaySubscription?> GetLatestIssueByUserFeatureAsync(Guid userId, string featureKey, DateTime endedSince);
    Task<PlaySubscription?> GetMappedByLinkedTokenAsync(string oldPurchaseToken);
    // Role change: Mapped -> Detached (RoleChanged) with the server action queued.
    Task<int> DetachForUserFeatureAsync(Guid userId, string featureKey, string pendingAction, string reason);
    // Account deletion: UserId null, Detached (AccountDeleted), server action queued on the still-Mapped rows.
    Task<int> AnonymiseForUserAsync(Guid userId, DateTime now, string pendingAction);
    // Admin deactivation: queues Cancel (stop renewals, no refund) on the user's Mapped subscriptions that still renew; they stay Mapped.
    Task<int> QueueCancelForUserAsync(Guid userId, string reason);
}
