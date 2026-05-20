using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class FeatureRepository(ApplicationDbContext context) : IFeatureRepository
{
    public async Task<AppFeature?> GetByKeyAsync(string key)
        => await context.AppFeatures.FirstOrDefaultAsync(f => f.Key == key);

    public async Task<IEnumerable<AppFeature>> GetAllAsync()
        => await context.AppFeatures.OrderBy(f => f.Key).ToListAsync();

    public async Task AddAsync(AppFeature feature)
        => await context.AppFeatures.AddAsync(feature);

    public async Task UpdateAsync(AppFeature feature)
    {
        context.AppFeatures.Update(feature);
        await context.SaveChangesAsync();
    }
}
