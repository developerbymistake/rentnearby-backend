namespace RentNearBy.Core.Interfaces;

public interface IListingPurgeService
{
    Task PurgeUserListingsAsync(Guid userId);

    Task PurgeUserListingsAsync(Guid userId, string listingKind);
}
