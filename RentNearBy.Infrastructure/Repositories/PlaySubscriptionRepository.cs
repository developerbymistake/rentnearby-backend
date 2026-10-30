using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class PlaySubscriptionRepository(ApplicationDbContext context) : IPlaySubscriptionRepository
{
    private static readonly string[] DueStates = [PlayStates.Active, PlayStates.InGracePeriod, PlayStates.Canceled];
    private static readonly string[] IssueStates = [PlayStates.OnHold, PlayStates.Paused, PlayStates.InGracePeriod];
    private static readonly string[] EndedStates = [PlayStates.Canceled, PlayStates.Expired];
    // A server action on a token Google already ended is a wasted call.
    private static readonly string[] NoActionStates = [PlayStates.Expired, PlayStates.PendingPurchaseCanceled];

    // Tracked and fresh: a stale tracked copy is detached first (lock-then-re-read, contract 3.5 step 3b).
    public async Task<PlaySubscription?> GetByTokenAsync(string purchaseToken)
    {
        DetachTracked(s => s.PurchaseToken == purchaseToken);
        return await context.PlaySubscriptions.FirstOrDefaultAsync(s => s.PurchaseToken == purchaseToken);
    }

    public async Task<PlaySubscription?> GetByIdAsync(Guid id)
    {
        DetachTracked(s => s.Id == id);
        return await context.PlaySubscriptions.FirstOrDefaultAsync(s => s.Id == id);
    }

    public async Task<List<PlaySubscription>> GetByIdsAsync(IReadOnlyCollection<Guid> ids)
    {
        if (ids.Count == 0) return [];
        var list = ids.Distinct().ToList();
        return await context.PlaySubscriptions.AsNoTracking()
            .Where(s => list.Contains(s.Id))
            .ToListAsync();
    }

    public async Task<List<PlaySubscription>> GetByUserAsync(Guid userId, int limit)
        => await context.PlaySubscriptions.AsNoTracking()
            .Where(s => s.UserId == userId)
            .OrderByDescending(s => s.CreatedAt)
            .Take(limit)
            .ToListAsync();

    public async Task<PlaySubscription?> GetMappedByUserFeatureAsync(Guid userId, string featureKey)
        => await context.PlaySubscriptions.AsNoTracking()
            .Where(s => s.UserId == userId && s.FeatureKey == featureKey && s.MappingStatus == PlayMappingStatuses.Mapped)
            .OrderByDescending(s => s.UpdatedAt)
            .FirstOrDefaultAsync();

    public async Task AddAsync(PlaySubscription row)
        => await context.PlaySubscriptions.AddAsync(row);

    public async Task LockByTokenAsync(string purchaseToken)
    {
        if (context.Database.CurrentTransaction == null)
            throw new InvalidOperationException("LockByTokenAsync requires an open transaction.");
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM \"PlaySubscriptions\" WHERE \"PurchaseToken\" = {purchaseToken} FOR UPDATE");
    }

    public async Task<bool> MarkAcknowledgedAsync(Guid id, DateTime at)
        => await context.PlaySubscriptions
            .Where(s => s.Id == id && s.AcknowledgedAt == null)
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.AcknowledgedAt, at)
                .SetProperty(s => s.AcknowledgementState, PlayAckStates.Acknowledged)
                .SetProperty(s => s.UpdatedAt, DateTime.UtcNow)) > 0;

    public async Task<List<Guid>> ListPendingActionsAsync(DateTime now, int limit)
        => await context.PlaySubscriptions.AsNoTracking()
            .Where(s => s.PendingServerAction != null && (s.NextActionAt == null || s.NextActionAt <= now))
            .OrderBy(s => s.NextActionAt ?? s.UpdatedAt)
            .Take(limit)
            .Select(s => s.Id)
            .ToListAsync();

    public async Task<List<Guid>> ListDueAsync(DateTime before, int limit)
        => await context.PlaySubscriptions.AsNoTracking()
            .Where(s => s.MappingStatus == PlayMappingStatuses.Mapped
                && DueStates.Contains(s.State)
                && s.ExpiryTime != null && s.ExpiryTime <= before)
            .OrderBy(s => s.ExpiryTime)
            .Take(limit)
            .Select(s => s.Id)
            .ToListAsync();

    public async Task<List<Guid>> ListAckPendingAsync(int limit)
        => await context.PlaySubscriptions.AsNoTracking()
            .Where(s => s.AcknowledgementState == PlayAckStates.Pending
                && s.AcknowledgedAt == null
                && s.MappingStatus == PlayMappingStatuses.Mapped
                && DueStates.Contains(s.State))
            .OrderBy(s => s.CreatedAt)
            .Take(limit)
            .Select(s => s.Id)
            .ToListAsync();

    public async Task<List<Guid>> ListStaleAsync(DateTime syncedBefore, int limit)
        => await context.PlaySubscriptions.AsNoTracking()
            .Where(s => s.MappingStatus == PlayMappingStatuses.Mapped
                && !NoActionStates.Contains(s.State)
                && (s.LastSyncedAt == null || s.LastSyncedAt < syncedBefore))
            .OrderBy(s => s.LastSyncedAt)
            .Take(limit)
            .Select(s => s.Id)
            .ToListAsync();

    public async Task<PlaySubscription?> GetLatestIssueByUserFeatureAsync(Guid userId, string featureKey, DateTime endedSince)
        => await context.PlaySubscriptions.AsNoTracking()
            .Where(s => s.UserId == userId && s.FeatureKey == featureKey
                && s.MappingStatus == PlayMappingStatuses.Mapped
                && (IssueStates.Contains(s.State)
                    || (EndedStates.Contains(s.State) && s.ExpiryTime != null && s.ExpiryTime >= endedSince)))
            .OrderByDescending(s => s.UpdatedAt)
            .FirstOrDefaultAsync();

    public async Task<PlaySubscription?> GetMappedByLinkedTokenAsync(string oldPurchaseToken)
        => await context.PlaySubscriptions.AsNoTracking()
            .Where(s => s.LinkedPurchaseToken == oldPurchaseToken && s.MappingStatus == PlayMappingStatuses.Mapped)
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync();

    public async Task<int> DetachForUserFeatureAsync(Guid userId, string featureKey, string pendingAction, string reason)
    {
        var now = DateTime.UtcNow;
        return await context.PlaySubscriptions
            .Where(s => s.UserId == userId && s.FeatureKey == featureKey && s.MappingStatus == PlayMappingStatuses.Mapped)
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.MappingStatus, PlayMappingStatuses.Detached)
                .SetProperty(s => s.DetachedReason, PlayDetachReasons.RoleChanged)
                .SetProperty(s => s.PendingServerAction, s => NoActionStates.Contains(s.State) ? null : pendingAction)
                .SetProperty(s => s.PendingActionReason, s => NoActionStates.Contains(s.State) ? null : reason)
                .SetProperty(s => s.PendingActionAttempts, 0)
                .SetProperty(s => s.NextActionAt, (DateTime?)null)
                .SetProperty(s => s.UpdatedAt, now));
    }

    public async Task<int> QueueCancelForUserAsync(Guid userId, string reason)
        => await context.PlaySubscriptions
            .Where(s => s.UserId == userId && s.MappingStatus == PlayMappingStatuses.Mapped
                && !NoActionStates.Contains(s.State) && s.State != PlayStates.Canceled
                && s.PendingServerAction == null)
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.PendingServerAction, PlayServerActions.Cancel)
                .SetProperty(s => s.PendingActionReason, reason)
                .SetProperty(s => s.PendingActionAttempts, 0)
                .SetProperty(s => s.NextActionAt, (DateTime?)null)
                .SetProperty(s => s.UpdatedAt, DateTime.UtcNow));

    // Only still-Mapped rows get Detached + the action; Rejected/Detached rows keep whatever action they already hold.
    public async Task<int> AnonymiseForUserAsync(Guid userId, DateTime now, string pendingAction)
    {
        var detached = await context.PlaySubscriptions
            .Where(s => s.UserId == userId && s.MappingStatus == PlayMappingStatuses.Mapped)
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.MappingStatus, PlayMappingStatuses.Detached)
                .SetProperty(s => s.DetachedReason, PlayDetachReasons.AccountDeleted)
                .SetProperty(s => s.PendingServerAction, s => NoActionStates.Contains(s.State) ? null : pendingAction)
                .SetProperty(s => s.PendingActionReason, s => NoActionStates.Contains(s.State) ? null : "Account deleted")
                .SetProperty(s => s.PendingActionAttempts, 0)
                .SetProperty(s => s.NextActionAt, (DateTime?)null));
        var anonymised = await context.PlaySubscriptions
            .Where(s => s.UserId == userId)
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.UserId, (Guid?)null)
                .SetProperty(s => s.AnonymisedAt, now)
                .SetProperty(s => s.UpdatedAt, now));
        return Math.Max(detached, anonymised);
    }

    private void DetachTracked(Func<PlaySubscription, bool> match)
    {
        foreach (var entry in context.ChangeTracker.Entries<PlaySubscription>()
                     .Where(e => e.State != EntityState.Added && match(e.Entity)).ToList())
            entry.State = EntityState.Detached;
    }
}
