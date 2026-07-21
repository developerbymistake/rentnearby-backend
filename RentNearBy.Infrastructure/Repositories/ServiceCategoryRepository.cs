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
}
