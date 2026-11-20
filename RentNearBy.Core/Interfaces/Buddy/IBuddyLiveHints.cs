namespace RentNearBy.Core.Interfaces.Buddy;

// Hints only, best effort, never throws.
public interface IBuddyLiveHints
{
    Task TripViewChangedAsync(Guid tripId, IReadOnlyCollection<Guid> userIds);

    Task TripExpensesChangedAsync(Guid tripId, long balancesVersion, IReadOnlyCollection<Guid> userIds);

    Task BuddiesChangedAsync(IReadOnlyCollection<Guid> userIds);
}
