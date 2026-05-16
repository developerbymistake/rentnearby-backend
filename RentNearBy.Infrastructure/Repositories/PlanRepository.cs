using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class PlanRepository(ApplicationDbContext context) : IPlanRepository
{
    public async Task<Plan?> GetByPlanTypeAsync(string planType)
        => await context.Plans.FirstOrDefaultAsync(p => p.PlanType == planType);

    public async Task<IEnumerable<Plan>> GetAllAsync()
        => await context.Plans.ToListAsync();

    public async Task AddAsync(Plan plan)
        => await context.Plans.AddAsync(plan);

    public async Task UpdateAsync(Plan plan)
        => context.Plans.Update(plan);

    public async Task DeleteAsync(Plan plan)
        => context.Plans.Remove(plan);
}
