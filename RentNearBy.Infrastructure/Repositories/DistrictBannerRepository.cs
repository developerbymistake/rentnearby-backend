using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class DistrictBannerRepository(ApplicationDbContext context)
    : Repository<DistrictBanner>(context), IDistrictBannerRepository
{
    public async Task<DistrictBanner?> GetActiveForUserAsync(Guid districtId, Guid userId)
        => await _dbSet.AsNoTracking()
            .Where(b => b.DistrictId == districtId
                     && b.IsActive
                     && !b.Dismissals.Any(d => d.UserId == userId))
            .FirstOrDefaultAsync();

    public async Task<DistrictBanner?> GetByDistrictIdAsync(Guid districtId)
        => await _dbSet.AsNoTracking()
            .FirstOrDefaultAsync(b => b.DistrictId == districtId);

    public async Task<IEnumerable<DistrictBanner>> GetAllWithDistrictAsync()
        => await _dbSet.AsNoTracking()
            .Include(b => b.District)
            .OrderByDescending(b => b.CreatedAt)
            .ToListAsync();
}
