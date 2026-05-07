using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class DistrictRepository(ApplicationDbContext context) : Repository<District>(context), IDistrictRepository
{
    public async Task<IEnumerable<District>> GetAllWithCitiesAsync()
        => await _dbSet.AsNoTracking()
            .Include(d => d.Cities)
            .OrderBy(d => d.Name)
            .ToListAsync();
}
