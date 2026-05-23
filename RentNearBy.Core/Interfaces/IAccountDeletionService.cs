namespace RentNearBy.Core.Interfaces;

public interface IAccountDeletionService
{
    Task DeleteAccountAsync(Guid userId);
}
