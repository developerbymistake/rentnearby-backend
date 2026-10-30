using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class PlanRepository(ApplicationDbContext context) : Repository<Plan>(context), IPlanRepository
{
    public async Task<List<Plan>> GetAllWithDetailsAsync(string? featureKey)
    {
        var query = _dbSet.AsNoTracking()
            .Include(p => p.Features)
            .Include(p => p.Prices)
            .AsSplitQuery();
        if (featureKey != null) query = query.Where(p => p.FeatureKey == featureKey);
        return await query
            .OrderBy(p => p.FeatureKey)
            .ThenBy(p => p.SortOrder)
            .ThenBy(p => p.Name)
            .ToListAsync();
    }

    public async Task<Plan?> GetWithDetailsAsync(Guid id)
        => await _dbSet
            .Include(p => p.Features)
            .Include(p => p.Prices)
            .AsSplitQuery()
            .FirstOrDefaultAsync(p => p.Id == id);

    public async Task<bool> CodeExistsAsync(string featureKey, string code, Guid? exceptId)
        => await _dbSet.AnyAsync(p => p.FeatureKey == featureKey && p.Code == code && (exceptId == null || p.Id != exceptId));

    // ix_planprices_play_baseplan is not deferrable, so base plan ids moving between cycles in one edit
    // (a swap) would fail row by row. Call inside the edit transaction, before ReplaceDetails.
    public async Task ReleaseChangedPlayIdsAsync(Plan plan, IEnumerable<PlanPrice> prices)
    {
        var incoming = prices.ToDictionary(p => p.CycleMonths, p => p.PlayBasePlanId);
        var released = false;
        foreach (var existing in plan.Prices)
        {
            if (existing.PlayBasePlanId == null) continue;
            if (incoming.TryGetValue(existing.CycleMonths, out var next) && next == existing.PlayBasePlanId) continue;
            existing.PlayBasePlanId = null;
            existing.PlayTrialOfferId = null;
            released = true;
        }
        if (released) await _context.SaveChangesAsync();
    }

    // Prices are matched by cycle and updated in place so their ids (PlaySubscriptions.PlanPriceId) survive an edit.
    public void ReplaceDetails(Plan plan, IEnumerable<PlanFeature> features, IEnumerable<PlanPrice> prices)
    {
        _context.PlanFeatures.RemoveRange(plan.Features);
        plan.Features.Clear();
        foreach (var f in features)
        {
            f.PlanId = plan.Id;
            plan.Features.Add(f);
        }

        var byCycle = plan.Prices.ToDictionary(p => p.CycleMonths);
        var keep = new HashSet<PlanPrice>();
        foreach (var p in prices)
        {
            if (byCycle.TryGetValue(p.CycleMonths, out var existing))
            {
                existing.PriceInr = p.PriceInr;
                existing.RazorpayPlanId = p.RazorpayPlanId;
                existing.PlayBasePlanId = p.PlayBasePlanId;
                existing.PlayTrialOfferId = p.PlayTrialOfferId;
                existing.IsEnabled = p.IsEnabled;
                keep.Add(existing);
            }
            else
            {
                p.PlanId = plan.Id;
                plan.Prices.Add(p);
                keep.Add(p);
            }
        }
        foreach (var stale in plan.Prices.Where(p => !keep.Contains(p)).ToList())
        {
            _context.PlanPrices.Remove(stale);
            plan.Prices.Remove(stale);
        }
    }

    public async Task<int> CountActiveHoldersAsync(Guid planId)
        => await _context.UserPlans.CountAsync(u => u.PlanId == planId && u.Status == UserPlanStatuses.Active);
}
