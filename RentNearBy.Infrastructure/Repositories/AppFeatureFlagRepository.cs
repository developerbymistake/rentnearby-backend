using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class AppFeatureFlagRepository(ApplicationDbContext context)
    : Repository<AppFeatureFlag>(context), IAppFeatureFlagRepository
{
    public async Task<AppFeatureFlag?> GetByKeyAsync(string key)
        => await context.AppFeatureFlags.FirstOrDefaultAsync(f => f.FeatureKey == key);
}
