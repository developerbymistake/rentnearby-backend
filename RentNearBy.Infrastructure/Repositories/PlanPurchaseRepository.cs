using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class PlanPurchaseRepository(ApplicationDbContext context) : IPlanPurchaseRepository
{
    public async Task AddAsync(PlanPurchase purchase)
        => await context.PlanPurchases.AddAsync(purchase);

    public async Task<PlanPurchase?> GetByIdAsync(Guid id)
        => await context.PlanPurchases.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);

    public async Task<PlanPurchase?> GetByOrderIdAsync(string razorpayOrderId)
        => await context.PlanPurchases.AsNoTracking().FirstOrDefaultAsync(p => p.RazorpayOrderId == razorpayOrderId);

    public async Task<PlanPurchase?> GetByPaymentIdAsync(string razorpayPaymentId)
        => await context.PlanPurchases.AsNoTracking().FirstOrDefaultAsync(p => p.RazorpayPaymentId == razorpayPaymentId);

    public async Task<PlanPurchase?> GetByIdempotencyKeyAsync(string key)
        => await context.PlanPurchases.AsNoTracking().FirstOrDefaultAsync(p => p.ClientIdempotencyKey == key);

    public async Task<PlanPurchase?> GetByPlayOrderIdAsync(string playOrderId)
        => await context.PlanPurchases.AsNoTracking().FirstOrDefaultAsync(p => p.PlayOrderId == playOrderId);

    public async Task<List<PlanPurchase>> GetByPlayOrderIdsAsync(IReadOnlyCollection<string> playOrderIds)
    {
        if (playOrderIds.Count == 0) return [];
        var ids = playOrderIds.Distinct().ToList();
        return await context.PlanPurchases.AsNoTracking()
            .Where(p => p.PlayOrderId != null && ids.Contains(p.PlayOrderId))
            .ToListAsync();
    }

    public async Task<PlanPurchase?> GetLatestGrantedBySubscriptionAsync(Guid playSubscriptionId)
        => await context.PlanPurchases.AsNoTracking()
            .Where(p => p.PlaySubscriptionId == playSubscriptionId
                && p.Provider == PaymentProviders.Play
                && p.Status == PurchaseStatuses.Granted
                && p.PlayOrderId != null)
            .OrderByDescending(p => p.GrantedAt ?? p.CreatedAt)
            .ThenByDescending(p => p.CreatedAt)
            .FirstOrDefaultAsync();

    public async Task<PlanPurchase?> GetOpenPlayByUserFeatureAsync(Guid userId, string featureKey, Guid planId, short cycleMonths)
        => await context.PlanPurchases.AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId && p.FeatureKey == featureKey
                && p.Provider == PaymentProviders.Play
                && p.PlanId == planId && p.CycleMonths == cycleMonths
                && (p.Status == PurchaseStatuses.Created || p.Status == PurchaseStatuses.Pending));

    public async Task<PlanPurchase?> GetOpenByUserFeatureAsync(Guid userId, string featureKey)
        => await context.PlanPurchases.AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId && p.FeatureKey == featureKey
                && (p.Status == PurchaseStatuses.Created || p.Status == PurchaseStatuses.Pending));

    // Play rows: Pending always (never time-expired), Created only inside the open-order window.
    public async Task<PlanPurchase?> GetLatestOpenByUserAsync(Guid userId)
    {
        var createdSince = DateTime.UtcNow - PlanRules.OpenOrderTtl;
        return await context.PlanPurchases.AsNoTracking()
            .Where(p => p.UserId == userId
                && ((p.Provider != PaymentProviders.Play
                        && (p.Status == PurchaseStatuses.Created || p.Status == PurchaseStatuses.Pending
                            || (p.Status == PurchaseStatuses.Paid && p.GrantBlockedReason == null))
                        && p.RazorpayOrderId != null)
                    || (p.Provider == PaymentProviders.Play
                        && (p.Status == PurchaseStatuses.Pending
                            || (p.Status == PurchaseStatuses.Created && p.CreatedAt > createdSince)))))
            .OrderByDescending(p => p.CreatedAt)
            .FirstOrDefaultAsync();
    }

    public async Task<List<PlanPurchase>> GetHistoryAsync(Guid userId, int limit)
        => await context.PlanPurchases.AsNoTracking()
            .Where(p => p.UserId == userId)
            .OrderByDescending(p => p.CreatedAt)
            .Take(limit)
            .ToListAsync();

    public async Task<List<PlanPurchase>> GetAdminPageAsync(
        string? status, Guid? userId, DateTime? afterCreatedAt, Guid? afterId, int limit)
    {
        var query = context.PlanPurchases.AsNoTracking();
        if (!string.IsNullOrEmpty(status)) query = query.Where(p => p.Status == status);
        if (userId.HasValue) query = query.Where(p => p.UserId == userId);
        if (afterCreatedAt.HasValue)
        {
            var at = afterCreatedAt.Value;
            var aid = afterId ?? Guid.Empty;
            query = query.Where(p => p.CreatedAt <= at && (p.CreatedAt < at || p.Id.CompareTo(aid) < 0));
        }
        return await query
            .OrderByDescending(p => p.CreatedAt)
            .ThenByDescending(p => p.Id)
            .Take(limit)
            .ToListAsync();
    }

    // AmountMismatch rows wait for the admin (recheck/refund), so they never take a reconcile slot.
    public async Task<List<(Guid Id, DateTime CreatedAt)>> ListIdsForReconcileAsync(
        string[] statuses, DateTime createdBefore, DateTime? createdAfter, int limit, bool newestFirst = false,
        string? providerNot = null)
    {
        var query = context.PlanPurchases.AsNoTracking()
            .Where(p => statuses.Contains(p.Status) && p.CreatedAt < createdBefore
                && (createdAfter == null || p.CreatedAt > createdAfter)
                && p.RazorpayOrderId != null
                && !(p.Status == PurchaseStatuses.Paid && p.GrantBlockedReason == GrantBlockedReasons.AmountMismatch));
        if (providerNot != null) query = query.Where(p => p.Provider != providerNot);
        query = newestFirst ? query.OrderByDescending(p => p.CreatedAt) : query.OrderBy(p => p.CreatedAt);
        var rows = await query
            .Take(limit)
            .Select(p => new { p.Id, p.CreatedAt })
            .ToListAsync();
        return rows.Select(r => (r.Id, r.CreatedAt)).ToList();
    }

    public async Task<bool> SetOrderIdIfCreatedAsync(Guid id, string razorpayOrderId)
        => await context.PlanPurchases
            .Where(p => p.Id == id && p.Status == PurchaseStatuses.Created && p.RazorpayOrderId == null)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.RazorpayOrderId, razorpayOrderId)) > 0;

    public async Task<bool> MarkPendingIfCreatedAsync(Guid id)
        => await context.PlanPurchases
            .Where(p => p.Id == id && p.Status == PurchaseStatuses.Created)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, PurchaseStatuses.Pending)) > 0;
}
