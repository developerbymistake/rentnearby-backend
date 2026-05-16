using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface IUserMembershipRepository
{
    Task AddAsync(UserMembership membership);
    Task<UserMembership?> GetActiveByUserIdAsync(Guid userId);
    Task<UserMembership?> GetByIdAsync(Guid id);
    Task<bool> HasUsedFreePlanAsync(Guid userId);
    Task SaveAsync();
}
