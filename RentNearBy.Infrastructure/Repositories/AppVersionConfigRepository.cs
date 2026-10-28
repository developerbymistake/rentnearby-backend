using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class AppVersionConfigRepository(ApplicationDbContext context)
    : Repository<AppVersionConfig>(context), IAppVersionConfigRepository
{
    public async Task<AppVersionConfig?> GetByPlatformAsync(string platform)
        => await context.AppVersionConfigs.FirstOrDefaultAsync(c => c.Platform == platform);

    public async Task<List<AppVersionConfig>> GetAllWithAdminAsync()
        => await context.AppVersionConfigs.AsNoTracking().Include(c => c.UpdatedByAdmin).ToListAsync();

    public async Task<bool> TryRaiseMaxSeenBuildAsync(string platform, int build)
        => await context.AppVersionConfigs
            .Where(c => c.Platform == platform && c.MaxSeenBuild < build)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.MaxSeenBuild, build)) > 0;
}
