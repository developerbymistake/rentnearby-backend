using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class CityRepository(ApplicationDbContext context) : Repository<City>(context), ICityRepository
{
    public async Task<IEnumerable<City>> GetAllWithDistrictsAsync()
        => await _dbSet.AsNoTracking()
            .Include(c => c.Districts)
            .OrderBy(c => c.Name)
            .ToListAsync();
}
