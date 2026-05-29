using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class RoomMembershipRepository(ApplicationDbContext context) : IRoomMembershipRepository
{
    public async Task AddAsync(RoomMembership membership)
        => await context.RoomMemberships.AddAsync(membership);

    public async Task<RoomMembership?> GetActiveByUserIdAsync(Guid userId)
        => await context.RoomMemberships
            .Where(m => m.UserId == userId && m.IsActive && m.ValidUntil > DateTime.UtcNow)
            .OrderByDescending(m => m.ValidUntil)
            .FirstOrDefaultAsync();

    public async Task<RoomMembership?> GetByIdAsync(Guid id)
        => await context.RoomMemberships.FirstOrDefaultAsync(m => m.Id == id);

    public async Task<bool> HasUsedFreePlanAsync(Guid userId)
    {
        var user = await context.Users.FindAsync(userId);
        return user?.HasUsedFreePlan ?? false;
    }

    public async Task<IEnumerable<RoomMembership>> GetExpiredAsync(DateTime beforeDate)
        => await context.RoomMemberships
            .Where(m => m.IsActive && m.ValidUntil < beforeDate)
            .ToListAsync();

    public async Task SaveAsync()
        => await context.SaveChangesAsync();
}
