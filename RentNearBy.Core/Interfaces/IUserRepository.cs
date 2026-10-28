using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface IUserRepository : IRepository<User>
{
    Task<User?> GetByPhoneAsync(string phoneNumber);
    Task<bool> PhoneExistsAsync(string phoneNumber);
    Task<User?> GetByVerifiedPhoneAsync(string phoneNumber);
    Task<bool> IsPhoneUsedByOtherUserAsync(string phoneNumber, Guid currentUserId);
    Task<bool> IsPhoneVerifiedByAnyUserAsync(string phoneNumber);
    Task<int> ConfirmRoleChoiceAsync(Guid userId, Guid roleId);
    Task<int> SetRoleAsync(Guid userId, Guid roleId);
}
