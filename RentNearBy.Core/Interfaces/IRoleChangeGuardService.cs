using RentNearBy.Core.Models;

namespace RentNearBy.Core.Interfaces;

public record BlockingListingCounts(int Rooms, int Plots)
{
    public int ForKind(string listingKind) => listingKind == ListingKinds.Room ? Rooms : Plots;
}

public interface IRoleChangeGuardService
{
    Task<BlockingListingCounts> GetBlockingCountsAsync(Guid userId);

    Task<IReadOnlyDictionary<Guid, BlockingListingCounts>> GetBlockingCountsAsync(IReadOnlyCollection<Guid> userIds);
}
