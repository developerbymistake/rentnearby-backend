using RentNearBy.Core.Models;

namespace RentNearBy.Core.Interfaces;

public record BlockingListingCounts(int Stays, int Taxis)
{
    public int ForKind(string listingKind) => listingKind switch
    {
        ListingKinds.Stay => Stays,
        ListingKinds.Taxi => Taxis,
        _ => 0,
    };
}

public interface IRoleChangeGuardService
{
    // One COUNT for the kind being left.
    Task<int> GetBlockingCountAsync(Guid userId, string leavingKind);

    Task<IReadOnlyDictionary<Guid, BlockingListingCounts>> GetBlockingCountsAsync(IReadOnlyCollection<Guid> userIds);
}
