using RentNearBy.Core.Models;

namespace RentNearBy.Core.Interfaces;

public record BlockingListingCounts(int Rooms, int Plots, int Stays)
{
    public int ForKind(string listingKind) => listingKind switch
    {
        ListingKinds.Room => Rooms,
        ListingKinds.Stay => Stays,
        _ => Plots,
    };
}

public interface IRoleChangeGuardService
{
    Task<BlockingListingCounts> GetBlockingCountsAsync(Guid userId);

    Task<IReadOnlyDictionary<Guid, BlockingListingCounts>> GetBlockingCountsAsync(IReadOnlyCollection<Guid> userIds);
}
