using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface IUsernameService
{
    // Existing user without a username gets one; idempotent and race safe. Sets user.Username.
    Task<string> EnsureAsync(User user);

    // Phone verified, no account yet: holds a username for the phone so the profile screen can show it.
    Task<string> ReserveForSignupAsync(string phoneNumber);

    // True while verify-otp's signup reservation for the phone exists (proof the phone was verified, single use, 24 h).
    Task<bool> HasSignupReservationAsync(string phoneNumber);

    // Same proof bound to the caller: Bound = held and the sent username equals it, Unbound = held and none sent, else None.
    Task<SignupProof> ClassifySignupAsync(string phoneNumber, string? username);

    // Adds the new user with the username held for its phone (or a fresh one) in the same save.
    Task SaveNewUserAsync(User newUser);
}

public enum SignupProof { None, Unbound, Bound }

public interface IPendingUsernameStore
{
    Task<string?> GetAsync(string phoneNumber);
    Task SaveAsync(string phoneNumber, string username, TimeSpan ttl);
    Task DeleteAsync(string phoneNumber);
}
