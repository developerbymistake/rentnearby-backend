using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface IUsernameService
{
    // Existing user without a username gets one; idempotent and race safe. Sets user.Username.
    Task<string> EnsureAsync(User user);

    // Phone verified, no account yet: holds a username for the phone so the profile screen can show it.
    Task<string> ReserveForSignupAsync(string phoneNumber);

    // Adds the new user with the username held for its phone (or a fresh one) in the same save.
    Task SaveNewUserAsync(User newUser);
}

public interface IPendingUsernameStore
{
    Task<string?> GetAsync(string phoneNumber);
    Task SaveAsync(string phoneNumber, string username, TimeSpan ttl);
    Task DeleteAsync(string phoneNumber);
}
