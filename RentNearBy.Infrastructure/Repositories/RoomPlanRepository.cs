using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class RoomPlanRepository(ApplicationDbContext context) : IRoomPlanRepository
{
    public async Task<RoomPlan?> GetByPlanTypeAsync(string planType)
        => await context.RoomPlans.FirstOrDefaultAsync(p => p.PlanType == planType);

    public async Task<IEnumerable<RoomPlan>> GetAllAsync()
        => await context.RoomPlans.ToListAsync();

    public async Task AddAsync(RoomPlan plan)
        => await context.RoomPlans.AddAsync(plan);

    public Task UpdateAsync(RoomPlan plan)
    {
        context.RoomPlans.Update(plan);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(RoomPlan plan)
    {
        context.RoomPlans.Remove(plan);
        return Task.CompletedTask;
    }
}
