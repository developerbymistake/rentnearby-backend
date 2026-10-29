using RentNearBy.Core.Models;

namespace RentNearBy.Core.Interfaces;

public record BlockingListingCounts(int Rooms, int Plots, int Stays, int Taxis)
{
    public int ForKind(string listingKind) => listingKind switch
    {
        ListingKinds.Room => Rooms,
        ListingKinds.Stay => Stays,
        ListingKinds.Taxi => Taxis,
        _ => Plots,
    };
}

public interface IRoleChangeGuardService
{
    // One COUNT for the kind being left; 0 while that kind's tab is off (leaving an off-tab role is always allowed).
    Task<int> GetBlockingCountAsync(Guid userId, string leavingKind);

    Task<IReadOnlyDictionary<Guid, BlockingListingCounts>> GetBlockingCountsAsync(IReadOnlyCollection<Guid> userIds);
}
