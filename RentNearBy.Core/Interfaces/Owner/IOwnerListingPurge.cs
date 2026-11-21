namespace RentNearBy.Core.Interfaces.Owner;

// Lane BE1. Inside the caller's transaction, set-based: calendar entries with their money notes, blocked ranges, special prices,
// weekly stats of the given listings. Called by ListingPurgeService (account delete, role purge) and DELETE /stays/me, /taxis/me.
public interface IOwnerListingPurge
{
    Task PurgeAsync(string listingKind, IReadOnlyCollection<Guid> listingIds, CancellationToken ct = default);
}
