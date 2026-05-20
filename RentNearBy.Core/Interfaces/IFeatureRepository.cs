using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface IFeatureRepository
{
    Task<AppFeature?> GetByKeyAsync(string key);
    Task<IEnumerable<AppFeature>> GetAllAsync();
    Task AddAsync(AppFeature feature);
    Task UpdateAsync(AppFeature feature);
}
