using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class PlotMembershipRepository(ApplicationDbContext context) : IPlotMembershipRepository
{
    public async Task AddAsync(PlotMembership membership)
        => await context.PlotMemberships.AddAsync(membership);

    public async Task<PlotMembership?> GetActiveByUserIdAsync(Guid userId)
        => await context.PlotMemberships
            .Where(m => m.UserId == userId && m.IsActive && m.ValidUntil > DateTime.UtcNow)
            .OrderByDescending(m => m.ValidUntil)
            .FirstOrDefaultAsync();

    public async Task<IReadOnlyList<PlotMembership>> GetExpiredPagedAsync(DateTime beforeDate, int page, int pageSize)
        => await context.PlotMemberships
            .Where(m => m.IsActive && m.ValidUntil < beforeDate)
            .OrderBy(m => m.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();
}
