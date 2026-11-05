namespace RentNearBy.Core.Interfaces;

public sealed record ExpireEffectsResult(int Paused, int Extended, int Reranked);

public sealed record RankReconcileResult(int Rows, IReadOnlyList<Guid> DistrictIds);

// Plan rank effects on Stay and Taxi listings: grant, expiry and the daily reconcile.
// Bulk methods never load listing rows and repeat the state predicate inside the UPDATE.
// None of these bust caches; callers bust per district after commit (IListingCacheInvalidator).
public interface ILiveSlotService
{
    // SELECT ... FOR UPDATE on the owner's Users row; requires an open transaction.
    Task LockOwnerAsync(Guid userId);

    // Stays and taxis.
    Task<int> SetRankAsync(Guid userId, string kind, short rank, bool featured);

    // Bring listings in line with the owner's current limits (after a grant/switch/renew).
    // Requires an open transaction and the owner lock.
    Task ApplyGrantEffectsAsync(Guid userId, string featureKey);

    // After a plan ends (expiry, revoke, refund). Requires an open transaction and the owner lock.
    Task<ExpireEffectsResult> ExpireEffectsAsync(Guid userId, string featureKey);

    // Daily drift repair: rows whose rank/featured differ from the owner's plan. Returns rows fixed and their
    // distinct districts for busting.
    Task<RankReconcileResult> ReconcileRanksAsync(string kind);
}
