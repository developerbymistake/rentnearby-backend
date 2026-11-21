namespace RentNearBy.Core.Interfaces;

public interface IListingPurgeService
{
    // Returns the deleted stay and taxi ids so the trip-enquiry closure can run for them.
    Task<(IReadOnlyList<Guid> StayIds, IReadOnlyList<Guid> TaxiIds)> PurgeUserListingsAsync(Guid userId);

    Task<IReadOnlyList<Guid>> DeleteUserListingRowsAsync(Guid userId, string listingKind);

    Task DeleteListingPhotosBestEffortAsync(Guid userId, IReadOnlyList<Guid> listingIds);

    // After the account deletion commit (NFR 4.2: never inline); a failure only orphans files.
    Task DeleteAllUserPhotosBestEffortAsync(Guid userId) => Task.CompletedTask;
}
