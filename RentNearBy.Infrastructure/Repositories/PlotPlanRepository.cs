using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class PlotPlanRepository(ApplicationDbContext context) : IPlotPlanRepository
{
    public async Task<PlotPlan?> GetByPlanTypeAsync(string planType)
        => await context.PlotPlans.FirstOrDefaultAsync(p => p.PlanType == planType);

    public async Task<IEnumerable<PlotPlan>> GetAllAsync()
        => await context.PlotPlans.OrderBy(p => p.Price).ToListAsync();
}
