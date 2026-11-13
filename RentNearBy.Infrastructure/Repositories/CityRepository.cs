using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class CityRepository(ApplicationDbContext context) : Repository<City>(context), ICityRepository
{
    private static readonly Expression<Func<City, CityDto>> ToDto = c => new CityDto
    {
        Id = c.Id,
        DistrictId = c.DistrictId,
        Name = c.Name,
        Latitude = c.Latitude,
        Longitude = c.Longitude,
    };

    public async Task<IEnumerable<City>> GetByDistrictIdAsync(Guid districtId)
        => await _dbSet.AsNoTracking()
            .Where(c => c.DistrictId == districtId)
            .OrderBy(c => c.Name)
            .ToListAsync();

    public Task<List<CityDto>> GetDtosByDistrictIdAsync(Guid districtId)
        => _dbSet.AsNoTracking()
            .Where(c => c.DistrictId == districtId)
            .OrderBy(c => c.Name)
            .Select(ToDto)
            .ToListAsync();

    public Task<List<CityDto>> GetAllDtosAsync()
        => _dbSet.AsNoTracking()
            .Select(ToDto)
            .ToListAsync();
}
