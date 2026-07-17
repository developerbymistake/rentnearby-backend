using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class CoinPlanRepository(ApplicationDbContext context)
    : Repository<CoinPlan>(context), ICoinPlanRepository
{
    public async Task<CoinPlan?> GetByFeatureKeyAndPlanTypeAsync(string featureKey, string planType)
        => await context.CoinPlans.FirstOrDefaultAsync(p => p.FeatureKey == featureKey && p.PlanType == planType);

    public async Task<IEnumerable<CoinPlan>> GetByFeatureKeyAsync(string featureKey)
        => await context.CoinPlans.Where(p => p.FeatureKey == featureKey).ToListAsync();
}
