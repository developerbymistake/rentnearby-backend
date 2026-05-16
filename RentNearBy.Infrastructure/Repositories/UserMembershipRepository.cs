using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class UserMembershipRepository(ApplicationDbContext context) : IUserMembershipRepository
{
    public async Task AddAsync(UserMembership membership)
        => await context.UserMemberships.AddAsync(membership);

    public async Task<UserMembership?> GetActiveByUserIdAsync(Guid userId)
        => await context.UserMemberships
            .Where(m => m.UserId == userId && m.IsActive && m.ValidUntil > DateTime.UtcNow)
            .OrderByDescending(m => m.ValidUntil)
            .FirstOrDefaultAsync();

    public async Task<UserMembership?> GetByIdAsync(Guid id)
        => await context.UserMemberships.FirstOrDefaultAsync(m => m.Id == id);

    public async Task<bool> HasUsedFreePlanAsync(Guid userId)
    {
        var user = await context.Users.FindAsync(userId);
        return user?.HasUsedFreePlan ?? false;
    }

    public async Task SaveAsync()
        => await context.SaveChangesAsync();
}
