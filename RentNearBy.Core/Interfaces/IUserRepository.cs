using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface IUserRepository : IRepository<User>
{
    Task<User?> GetByPhoneAsync(string phoneNumber);
    // Bakhli social: the user's profile gender, null = no UserProfiles row (onboarding not done).
    Task<string?> GetSocialGenderAsync(Guid userId);
    Task<bool> PhoneExistsAsync(string phoneNumber);
    Task<User?> GetByVerifiedPhoneAsync(string phoneNumber);
    Task<bool> IsPhoneUsedByOtherUserAsync(string phoneNumber, Guid currentUserId);
    Task<bool> IsPhoneVerifiedByAnyUserAsync(string phoneNumber);
    Task<int> ConfirmRoleChoiceAsync(Guid userId, Guid roleId);
    Task<int> SetRoleAsync(Guid userId, Guid roleId);
    Task<int> CorrectRoleAsync(Guid userId, Guid fromRoleId, Guid toRoleId);
    Task<int> LockIfRoleAsync(Guid userId, Guid roleId);
    // Writes Users.PlayAccountKey only while it is null; false = already set (or no such user).
    Task<bool> SetPlayAccountKeyIfNullAsync(Guid userId, string playAccountKey);
    Task<Guid?> GetIdByPlayAccountKeyAsync(string playAccountKey);
    // Writes Users.Username only while it is null; false = already set (or no such user).
    Task<bool> SetUsernameIfNullAsync(Guid userId, string username);
    Task<string?> GetUsernameAsync(Guid userId);
    Task<bool> UsernameExistsAsync(string username);
}
