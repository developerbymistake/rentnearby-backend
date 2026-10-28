using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class RoleRepository(ApplicationDbContext context) : Repository<Role>(context), IRoleRepository
{
    public async Task<IReadOnlyList<Role>> GetActiveOrderedAsync()
        => await _dbSet.AsNoTracking()
            .Where(r => r.IsActive)
            .OrderBy(r => r.SortOrder)
            .ToListAsync();

    public async Task<Role?> GetActiveByKeyAsync(string key)
        => await _dbSet.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Key == key && r.IsActive);
}
