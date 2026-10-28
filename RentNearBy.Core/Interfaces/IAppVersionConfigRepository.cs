using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface IAppVersionConfigRepository : IRepository<AppVersionConfig>
{
    Task<AppVersionConfig?> GetByPlatformAsync(string platform);
    Task<List<AppVersionConfig>> GetAllWithAdminAsync();
    Task<bool> TryRaiseMaxSeenBuildAsync(string platform, int build);
}
