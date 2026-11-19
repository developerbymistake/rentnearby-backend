using RentNearBy.Core.Models;

namespace RentNearBy.Core.Interfaces;

public interface IAccountDeletionService
{
    // Refused (nothing written, Reasons filled) while the user has anything active.
    Task<AccountDeletionResult> DeleteAccountAsync(Guid userId);
}
