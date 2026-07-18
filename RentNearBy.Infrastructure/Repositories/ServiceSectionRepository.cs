using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class ServiceSectionRepository(ApplicationDbContext context)
    : Repository<ServiceSection>(context), IServiceSectionRepository
{
    public async Task<IEnumerable<ServiceSection>> GetAllOrderedAsync()
        => await _dbSet.AsNoTracking()
            .OrderBy(s => s.SortOrder).ThenBy(s => s.Name)
            .ToListAsync();
}
