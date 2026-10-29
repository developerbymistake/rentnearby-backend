namespace RentNearBy.Core.Interfaces;

// Redis busting for every public feed a rank/visibility change can touch. Call after commit; best-effort.
public interface IListingCacheInvalidator
{
    // Districts come from one DISTINCT query over the user's rows of that kind.
    Task BustForUserAsync(Guid userId, string kind);

    // Batch form: one DISTINCT query over all the users' rows of that kind, then one bust.
    Task BustForUsersAsync(IReadOnlyCollection<Guid> userIds, string kind);

    Task BustDistrictsAsync(string kind, IEnumerable<Guid> districtIds);
}
