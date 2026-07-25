using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class AgentRepository(ApplicationDbContext context)
    : Repository<Agent>(context), IAgentRepository
{
    public async Task<IEnumerable<Agent>> GetAllWithServicesAsync()
        => await _dbSet.AsNoTracking()
            .Include(a => a.AgentServices).ThenInclude(as_ => as_.Service)
            .Include(a => a.User)
            .OrderBy(a => a.Name)
            .ToListAsync();

    public async Task<Agent?> GetByIdWithServicesAsync(Guid id)
        => await _dbSet.AsNoTracking()
            .Include(a => a.AgentServices).ThenInclude(as_ => as_.Service)
            .Include(a => a.User)
            .FirstOrDefaultAsync(a => a.Id == id);

    public async Task<IEnumerable<Agent>> GetActiveByServiceIdAsync(Guid serviceId)
        => await _dbSet.AsNoTracking()
            .Where(a => a.IsActive && a.AgentServices.Any(as_ => as_.ServiceId == serviceId))
            .OrderBy(a => a.Name)
            .ToListAsync();

    public async Task<Agent?> GetByUserIdAsync(Guid userId)
        => await _dbSet.AsNoTracking()
            .FirstOrDefaultAsync(a => a.UserId == userId && a.IsActive);

    public async Task<bool> ExistsByUserIdAsync(Guid userId)
        => await _dbSet.AsNoTracking().AnyAsync(a => a.UserId == userId);
}
