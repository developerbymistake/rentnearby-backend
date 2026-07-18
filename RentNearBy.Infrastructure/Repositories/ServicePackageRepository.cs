using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class ServicePackageRepository(ApplicationDbContext context)
    : Repository<ServicePackage>(context), IServicePackageRepository
{
    // Includes PackageInclusions -> Inclusion so the list endpoint backing the consumer Package List
    // screen (which shows Inclusion chips per package, not just a bare preview) can map straight to
    // ServicePackageDto without a second round-trip per package.
    public async Task<IEnumerable<ServicePackage>> GetByServiceIdAsync(Guid? serviceId)
        => await _dbSet.AsNoTracking()
            .Include(p => p.PackageInclusions).ThenInclude(pi => pi.Inclusion)
            .Where(p => serviceId == null || p.ServiceId == serviceId)
            .OrderBy(p => p.SortOrder).ThenBy(p => p.Name)
            .ToListAsync();

    public async Task<ServicePackage?> GetByIdWithInclusionsAsync(Guid id)
        => await _dbSet.AsNoTracking()
            .Include(p => p.PackageInclusions).ThenInclude(pi => pi.Inclusion)
            .FirstOrDefaultAsync(p => p.Id == id);
}
