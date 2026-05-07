using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class DistrictRepository(ApplicationDbContext context) : Repository<District>(context), IDistrictRepository
{
    public async Task<IEnumerable<District>> GetByCityIdAsync(Guid cityId)
        => await _dbSet.AsNoTracking()
            .Where(d => d.CityId == cityId)
            .OrderBy(d => d.Name)
            .ToListAsync();
}
