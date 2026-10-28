namespace RentNearBy.Core.Interfaces;

public interface IListingPurgeService
{
    Task PurgeUserListingsAsync(Guid userId);

    Task<IReadOnlyList<Guid>> DeleteUserListingRowsAsync(Guid userId, string listingKind);

    Task DeleteListingPhotosBestEffortAsync(Guid userId, IReadOnlyList<Guid> listingIds);
}
