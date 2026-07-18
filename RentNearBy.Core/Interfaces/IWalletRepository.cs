namespace RentNearBy.Core.Interfaces;

public interface IWalletRepository
{
    Task EnsureExistsAsync(Guid userId);

    // Atomic UPDATE ... WHERE Balance >= amount — returns the new balance on success, null if the
    // balance was insufficient. No read-then-write step exists to race against a concurrent debit.
    Task<int?> TryDebitAsync(Guid userId, int amount);

    Task<int> CreditAsync(Guid userId, int amount);
    Task<int> GetBalanceAsync(Guid userId);
}
