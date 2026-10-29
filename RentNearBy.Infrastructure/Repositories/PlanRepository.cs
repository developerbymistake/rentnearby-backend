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

    public void ReplaceDetails(Plan plan, IEnumerable<PlanFeature> features, IEnumerable<PlanPrice> prices)
    {
        _context.PlanFeatures.RemoveRange(plan.Features);
        _context.PlanPrices.RemoveRange(plan.Prices);
        plan.Features.Clear();
        plan.Prices.Clear();
        foreach (var f in features)
        {
            f.PlanId = plan.Id;
            plan.Features.Add(f);
        }
        foreach (var p in prices)
        {
            p.PlanId = plan.Id;
            plan.Prices.Add(p);
        }
    }

    public async Task<int> CountActiveHoldersAsync(Guid planId)
        => await _context.UserPlans.CountAsync(u => u.PlanId == planId && u.Status == UserPlanStatuses.Active);
}
