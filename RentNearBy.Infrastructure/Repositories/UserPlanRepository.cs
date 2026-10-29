using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class UserPlanRepository(ApplicationDbContext context) : Repository<UserPlan>(context), IUserPlanRepository
{
    public async Task<UserPlan?> GetActiveAsync(Guid userId, string featureKey)
    {
        var now = DateTime.UtcNow;
        return await _dbSet.AsNoTracking()
            .FirstOrDefaultAsync(u => u.UserId == userId && u.FeatureKey == featureKey
                && u.Status == UserPlanStatuses.Active && u.EndsAt > now);
    }

    public async Task<List<UserPlan>> GetHistoryAsync(Guid userId, int limit)
        => await _dbSet.AsNoTracking()
            .Include(u => u.Plan)
            .Where(u => u.UserId == userId)
            .OrderByDescending(u => u.CreatedAt)
            .Take(limit)
            .ToListAsync();

    public async Task<bool> HasAnyAsync(Guid userId, string featureKey)
        => await _dbSet.AnyAsync(u => u.UserId == userId && u.FeatureKey == featureKey);
}
