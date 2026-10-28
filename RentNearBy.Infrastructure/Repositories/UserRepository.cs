using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class UserRepository(ApplicationDbContext context) : Repository<User>(context), IUserRepository
{
    public async Task<User?> GetByPhoneAsync(string phoneNumber)
        => await _dbSet.AsNoTracking()
            .FirstOrDefaultAsync(u => u.PhoneNumber == phoneNumber);

    public async Task<bool> PhoneExistsAsync(string phoneNumber)
        => await _dbSet.AnyAsync(u => u.PhoneNumber == phoneNumber);

    public async Task<User?> GetByVerifiedPhoneAsync(string phoneNumber)
        => await _dbSet.AsNoTracking()
            .FirstOrDefaultAsync(u => u.PhoneNumber == phoneNumber && u.IsPhoneVerified);

    public async Task<bool> IsPhoneUsedByOtherUserAsync(string phoneNumber, Guid currentUserId)
        => await _dbSet.AnyAsync(u => u.PhoneNumber == phoneNumber && u.Id != currentUserId);

    public async Task<bool> IsPhoneVerifiedByAnyUserAsync(string phoneNumber)
        => await _dbSet.AnyAsync(u => u.PhoneNumber == phoneNumber && u.IsPhoneVerified);

    public async Task<int> ConfirmRoleChoiceAsync(Guid userId, Guid roleId)
    {
        var contactVisible = IsContactVisibleForRole(roleId);
        return await _dbSet
            .Where(u => u.Id == userId && u.RoleConfirmedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(u => u.RoleId, roleId)
                .SetProperty(u => u.IsContactVisible, contactVisible)
                .SetProperty(u => u.RoleConfirmedAt, DateTime.UtcNow)
                .SetProperty(u => u.UpdatedAt, DateTime.UtcNow));
    }

    // An admin-set role counts as confirmed, so the first-login role page can never override it.
    public async Task<int> SetRoleAsync(Guid userId, Guid roleId)
    {
        var contactVisible = IsContactVisibleForRole(roleId);
        return await _dbSet
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(u => u.RoleId, roleId)
                .SetProperty(u => u.IsContactVisible, contactVisible)
                .SetProperty(u => u.RoleConfirmedAt, u => u.RoleConfirmedAt ?? DateTime.UtcNow)
                .SetProperty(u => u.UpdatedAt, DateTime.UtcNow));
    }

    // Owners always show their number; a Seeker starts hidden.
    private static bool IsContactVisibleForRole(Guid roleId) => UserRoles.ListingKindOf(roleId) != null;
}
