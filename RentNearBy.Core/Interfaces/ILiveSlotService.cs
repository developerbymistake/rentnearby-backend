namespace RentNearBy.Core.Interfaces;

public sealed record ExpireEffectsResult(int Paused, int Extended, int Reranked);

// Count/claim logic for room and plot slots plus the set-based listing effects of plan changes.
// Every create/activation of a room/plot: transaction -> LockOwnerAsync -> GetLimits -> count -> mutate -> commit.
// Bulk methods never load listing rows and repeat the state predicate inside the UPDATE.
// None of these bust caches; callers bust per district after commit (IListingCacheInvalidator).
public interface ILiveSlotService
{
    // SELECT ... FOR UPDATE on the owner's Users row; requires an open transaction.
    Task LockOwnerAsync(Guid userId);

    // Creation limit: not deleted and not Rejected (paused, pending and drafts count).
    Task<int> CountForCreationAsync(Guid userId, string kind);

    // Live: not deleted and (IsActive or a Pending Go Live).
    Task<int> CountLiveAsync(Guid userId, string kind, Guid? excludeId = null);

    Task<int> CountPlanPausedAsync(Guid userId, string kind);

    // Resumes up to `slots` PauseReason=Plan rows, newest first.
    Task<int> ResumePlanPausedAsync(Guid userId, string kind, int slots, DateTime validUntil);

    // Keeps the newest `keep` rows among (IsActive or Pending) and plan-pauses the other live rows.
    Task<int> PlanPauseExtraAsync(Guid userId, string kind, int keep);

    // Rooms, plots, stays and taxis.
    Task<int> SetRankAsync(Guid userId, string kind, short rank, bool featured);

    // ValidUntil of every IsActive row of the kind.
    Task<int> ExtendValidUntilAsync(Guid userId, string kind, DateTime validUntil);

    // Bring listings in line with the owner's current limits (after a grant/switch/renew).
    // Requires an open transaction and the owner lock.
    Task ApplyGrantEffectsAsync(Guid userId, string featureKey);

    // After a plan ends (expiry, revoke, refund). Requires an open transaction and the owner lock.
    Task<ExpireEffectsResult> ExpireEffectsAsync(Guid userId, string featureKey);

    // A live or Pending row stopped counting (delete, pause, reject, takedown, admin deactivate): resume the
    // newest plan-paused rows into the free slots. Requires tx + lock.
    Task<int> ResumeAfterSlotFreedAsync(Guid userId, string kind);

    // Admin suspend/unsuspend of a user's rooms and plots. Unsuspend requires tx + lock.
    Task<int> SuspendAsync(Guid userId);
    Task<int> UnsuspendAsync(Guid userId);

    // Daily drift repair: rows whose rank/featured differ from the owner's plan. Returns rows fixed.
    Task<int> ReconcileRanksAsync(string kind);
}
