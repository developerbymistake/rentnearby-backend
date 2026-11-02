using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class DistrictRepository(ApplicationDbContext context) : Repository<District>(context), IDistrictRepository
{
    public async Task<IEnumerable<District>> GetAllWithCitiesAsync()
        => await _dbSet.AsNoTracking()
            .Include(d => d.Cities.OrderBy(c => c.Name))
            .OrderBy(d => d.Name)
            .ToListAsync();

    public async Task<Guid?> FindActiveContainingAsync(double latitude, double longitude)
    {
        var point = new Point(longitude, latitude) { SRID = 4326 };
        return await _dbSet.AsNoTracking()
            .Where(d => d.IsActive && d.Boundary != null && d.Boundary.Contains(point))
            .Select(d => (Guid?)d.Id)
            .FirstOrDefaultAsync();
    }
}
