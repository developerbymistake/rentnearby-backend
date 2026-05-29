using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface IUserRepository : IRepository<User>
{
    Task<User?> GetByPhoneAsync(string phoneNumber);
    Task<bool> PhoneExistsAsync(string phoneNumber);
    Task<User?> GetByGoogleIdAsync(string googleId);
    Task<bool> GoogleIdExistsAsync(string googleId);
    Task<bool> IsPhoneVerifiedByOtherUserAsync(string phoneNumber, Guid currentUserId);
}
