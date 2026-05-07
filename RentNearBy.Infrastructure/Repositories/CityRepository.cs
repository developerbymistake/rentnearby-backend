using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class CityRepository(ApplicationDbContext context) : Repository<City>(context), ICityRepository
{
    public async Task<IEnumerable<City>> GetByDistrictIdAsync(Guid districtId)
        => await _dbSet.AsNoTracking()
            .Where(c => c.DistrictId == districtId)
            .OrderBy(c => c.Name)
            .ToListAsync();
}
