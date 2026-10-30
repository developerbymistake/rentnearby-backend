using RentNearBy.Core.Entities;
using RentNearBy.Core.Models;

namespace RentNearBy.Core.Interfaces;

public interface IPlayEntitlementApplier
{
    // Caller holds the transaction, the owner lock and the PlaySubscriptions row lock. Entitled snapshots only.
    Task<PlayApplyResult> ApplyEntitledInTxAsync(PlaySubscription subscription, PlaySubscriptionSnapshot snapshot,
        PlayResolvedTarget target, PlanPurchase? openPurchase, string source);
    // After commit: publish, cache bust, acknowledge.
    Task AfterCommitAsync(PlayApplyResult result);
    // Reconciler retry; true when acknowledged now or already.
    Task<bool> TryAcknowledgeAsync(Guid playSubscriptionId);
}
