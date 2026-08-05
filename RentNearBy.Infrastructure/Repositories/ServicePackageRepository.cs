using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class ServicePackageRepository(ApplicationDbContext context)
    : Repository<ServicePackage>(context), IServicePackageRepository
{
    public async Task<IEnumerable<ServicePackage>> GetByServiceIdAsync(Guid? serviceId)
        => await _dbSet.AsNoTracking()
            .Where(p => serviceId == null || p.ServiceId == serviceId)
            .OrderBy(p => p.SortOrder).ThenBy(p => p.Name)
            .ToListAsync();
}
