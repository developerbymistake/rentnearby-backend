using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class ServiceCategoryRepository(ApplicationDbContext context)
    : Repository<ServiceCategory>(context), IServiceCategoryRepository
{
    public async Task<IEnumerable<ServiceCategory>> GetAllOrderedAsync()
        => await _dbSet.AsNoTracking()
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
            .ToListAsync();

    public async Task<IEnumerable<(ServiceCategory Category, int ServiceCount)>> GetAllOrderedWithServiceCountAsync()
        => (await _dbSet.AsNoTracking()
                .OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
                .Select(c => new { Category = c, ServiceCount = c.Services.Count(s => s.IsActive) })
                .ToListAsync())
            .Select(x => (x.Category, x.ServiceCount));
}
