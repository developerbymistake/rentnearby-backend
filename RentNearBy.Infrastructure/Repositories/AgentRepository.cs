using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class AgentRepository(ApplicationDbContext context)
    : Repository<Agent>(context), IAgentRepository
{
    public async Task<IEnumerable<Agent>> GetAllWithCategoriesAsync()
        => await _dbSet.AsNoTracking()
            .Include(a => a.AgentServiceCategories).ThenInclude(ac => ac.ServiceCategory)
            .OrderBy(a => a.Name)
            .ToListAsync();

    public async Task<Agent?> GetByIdWithCategoriesAsync(Guid id)
        => await _dbSet.AsNoTracking()
            .Include(a => a.AgentServiceCategories).ThenInclude(ac => ac.ServiceCategory)
            .FirstOrDefaultAsync(a => a.Id == id);

    public async Task<IEnumerable<Agent>> GetActiveByServiceCategoryIdAsync(Guid serviceCategoryId)
        => await _dbSet.AsNoTracking()
            .Where(a => a.IsActive && a.AgentServiceCategories.Any(ac => ac.ServiceCategoryId == serviceCategoryId))
            .OrderBy(a => a.Name)
            .ToListAsync();
}
