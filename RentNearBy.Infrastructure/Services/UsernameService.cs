using Npgsql;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;

namespace RentNearBy.Infrastructure.Services;

public sealed class UsernameService(IUnitOfWork unitOfWork, IPendingUsernameStore pending) : IUsernameService
{
    internal Func<string> NextCandidate { get; init; } = Usernames.NewRandom;

    public async Task<string> EnsureAsync(User user)
    {
        if (user.Username != null) return user.Username;

        user.Username = await Usernames.AssignAsync(async candidate =>
        {
            try
            {
                if (await unitOfWork.Users.SetUsernameIfNullAsync(user.Id, candidate)) return candidate;
            }
            catch (Exception ex) when (IsUsernameClash(ex))
            {
                return null;
            }
            // Another request set it first: theirs is the one.
            return await unitOfWork.Users.GetUsernameAsync(user.Id)
                ?? throw new KeyNotFoundException("User not found");
        }, NextCandidate);
        return user.Username;
    }

    public async Task<string> ReserveForSignupAsync(string phoneNumber)
    {
        var held = await pending.GetAsync(phoneNumber);
        var username = await Usernames.AssignAsync(
            async candidate => await unitOfWork.Users.UsernameExistsAsync(candidate) ? null : candidate,
            NextCandidate,
            held);
        await pending.SaveAsync(phoneNumber, username, Usernames.ReservationTtl);
        return username;
    }

    public async Task<bool> HasSignupReservationAsync(string phoneNumber)
        => await pending.GetAsync(phoneNumber) != null;

    public async Task SaveNewUserAsync(User newUser)
    {
        var held = await pending.GetAsync(newUser.PhoneNumber);
        await unitOfWork.Users.AddAsync(newUser);
        await Usernames.AssignAsync(async candidate =>
        {
            newUser.Username = candidate;
            try
            {
                await unitOfWork.SaveChangesAsync();
                return candidate;
            }
            catch (Exception ex) when (IsUsernameClash(ex))
            {
                return null;
            }
        }, NextCandidate, held);
        await pending.DeleteAsync(newUser.PhoneNumber);
    }

    public static bool IsUsernameClash(Exception ex)
    {
        for (Exception? e = ex; e != null; e = e.InnerException)
            if (e is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg && pg.ConstraintName == Usernames.UniqueIndex)
                return true;
        return false;
    }
}
