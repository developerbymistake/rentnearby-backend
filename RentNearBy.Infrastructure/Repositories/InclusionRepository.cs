using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class InclusionRepository(ApplicationDbContext context)
    : Repository<Inclusion>(context), IInclusionRepository
{
    public async Task<IEnumerable<Inclusion>> GetAllOrderedAsync()
        => await _dbSet.AsNoTracking()
            .OrderBy(i => i.SortOrder).ThenBy(i => i.Name)
            .ToListAsync();
}
