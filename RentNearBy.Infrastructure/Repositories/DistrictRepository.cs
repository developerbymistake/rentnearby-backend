using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using RentNearBy.Core.DTOs.Responses;
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

    public Task<List<DistrictDto>> GetAllDtosAsync()
        => _dbSet.AsNoTracking()
            .Select(d => new DistrictDto
            {
                Id = d.Id,
                Name = d.Name,
                StateName = d.StateName,
                IsActive = d.IsActive,
                BusinessOnboardingEnabled = d.BusinessOnboardingEnabled,
            })
            .ToListAsync();

    public async Task<Guid?> FindActiveContainingAsync(double latitude, double longitude)
    {
        var point = new Point(longitude, latitude) { SRID = 4326 };
        var inside = await _dbSet.AsNoTracking()
            .Where(d => d.IsActive && d.Boundary != null && d.Boundary.Contains(point))
            .Select(d => (Guid?)d.Id)
            .FirstOrDefaultAsync();
        if (inside != null) return inside;

        // A city centre can sit just outside a simplified border: take the nearest active district within about 5 km.
        return await _dbSet.AsNoTracking()
            .Where(d => d.IsActive && d.Boundary != null && d.Boundary.IsWithinDistance(point, NearDegrees))
            .OrderBy(d => d.Boundary!.Distance(point))
            .Select(d => (Guid?)d.Id)
            .FirstOrDefaultAsync();
    }

    private const double NearDegrees = 0.05;
}
