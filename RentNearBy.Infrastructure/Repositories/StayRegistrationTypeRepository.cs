using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class StayRegistrationTypeRepository(ApplicationDbContext context)
    : Repository<StayRegistrationType>(context), IStayRegistrationTypeRepository
{
    public async Task<IReadOnlyList<StayRegistrationType>> GetActiveAsync()
        => await _dbSet.AsNoTracking().Where(t => t.IsActive).ToListAsync();
}
