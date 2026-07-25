using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface IAppFeatureFlagRepository : IRepository<AppFeatureFlag>
{
    Task<AppFeatureFlag?> GetByKeyAsync(string key);
}
