namespace RentNearBy.Core.Interfaces;

public sealed record ExpireEffectsResult(int Paused, int Extended, int Reranked);

public sealed record RankReconcileResult(int Rows, IReadOnlyList<Guid> DistrictIds);

public sealed record LimitsReconcileSummary(int Listings, int Notified, IReadOnlyList<Guid> DistrictIds);

// Plan rank effects on Stay and Taxi listings: grant, expiry and the daily reconcile.
// Bulk methods never load listing rows and repeat the state predicate inside the UPDATE.
// None of these bust caches; callers bust per district after commit (IListingCacheInvalidator).
public interface ILiveSlotService
{
    // SELECT ... FOR UPDATE on the owner's Users row; requires an open transaction.
    Task LockOwnerAsync(Guid userId);

    // Stays and taxis. The four-argument form takes the ring weight from the owner's entitlement (taxi).
    Task<int> SetRankAsync(Guid userId, string kind, short rank, bool featured);

    // The only writer of TaxiListings.RingWeight (contract 7.1); ignored for stays.
    Task<int> SetRankAsync(Guid userId, string kind, short rank, bool featured, int ringWeight);

    // Bring listings in line with the owner's current limits (after a grant/switch/renew).
    // Requires an open transaction and the owner lock.
    Task ApplyGrantEffectsAsync(Guid userId, string featureKey);

    // After a plan ends (expiry, revoke, refund). Requires an open transaction and the owner lock.
    Task<ExpireEffectsResult> ExpireEffectsAsync(Guid userId, string featureKey);

    // Daily drift repair: rows whose rank/featured differ from the owner's plan. Returns rows fixed and their
    // distinct districts for busting.
    Task<RankReconcileResult> ReconcileRanksAsync(string kind);

    // Daily limits reconcile and the payment switch (contract 3.4): rows hidden or shown to match each owner's plan, nothing
    // hidden while payment is OFF, PlanLimitsChanged for owners who lost something. Opens its own transactions.
    Task<LimitsReconcileSummary> ReconcileLimitsAsync(string kind, CancellationToken ct = default);

    // After the caller's commit of a grant or expiry: matcher wake and request hints of offers closed on hidden routes. Never throws.
    Task AfterCommitAsync();
}
