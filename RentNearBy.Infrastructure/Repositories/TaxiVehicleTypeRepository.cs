using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class TaxiVehicleTypeRepository(ApplicationDbContext context) : Repository<TaxiVehicleType>(context), ITaxiVehicleTypeRepository
{
    public async Task<IReadOnlyList<TaxiVehicleType>> GetActiveAsync()
        => await _dbSet.AsNoTracking().Where(t => t.IsActive).ToListAsync();
}
