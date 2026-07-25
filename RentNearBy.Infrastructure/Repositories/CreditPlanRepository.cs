using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class CreditPlanRepository(ApplicationDbContext context)
    : Repository<CreditPlan>(context), ICreditPlanRepository
{
    public async Task<CreditPlan?> GetByFeatureKeyAndPlanTypeAsync(string featureKey, string planType)
        => await context.CreditPlans.FirstOrDefaultAsync(p => p.FeatureKey == featureKey && p.PlanType == planType);

    public async Task<IEnumerable<CreditPlan>> GetByFeatureKeyAsync(string featureKey)
        => await context.CreditPlans.Where(p => p.FeatureKey == featureKey).ToListAsync();
}
