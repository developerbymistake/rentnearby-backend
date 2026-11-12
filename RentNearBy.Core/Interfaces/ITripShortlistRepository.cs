using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface ITripShortlistRepository
{
    // AsNoTracking: Active items plus Closed/NoReply items of the last 30 days, oldest first.
    Task<IReadOnlyList<TripShortlistItem>> GetForTripAsync(Guid tripId, DateTime nowUtc);
    // AsNoTracking: Active items of one step, oldest first.
    Task<IReadOnlyList<TripShortlistItem>> GetActiveStepAsync(Guid tripId, string stepKey);
    // Tracked: the Active row of this listing in the step.
    Task<TripShortlistItem?> FindActiveAsync(Guid tripId, string stepKey, Guid listingId);
    // Tracked: the newest NoReply-closed row of this listing in the step (reopened by a new add).
    Task<TripShortlistItem?> FindNoReplyAsync(Guid tripId, string stepKey, Guid listingId);
    Task<int> CountActiveAsync(Guid tripId, string stepKey);
    void Add(TripShortlistItem item);

    // Set-based Active -> Closed; return rows changed.
    Task<int> CloseOneAsync(Guid tripId, string stepKey, Guid listingId, string reason, DateTime nowUtc);
    Task<int> CloseStepAsync(Guid tripId, string stepKey, string reason, DateTime nowUtc);
    // Active -> Sent when an enquiry went out. stepKey null matches the listing in any step of the trip.
    Task<int> MarkSentAsync(Guid tripId, string? stepKey, IReadOnlyCollection<Guid> listingIds, DateTime nowUtc);
    // Active and Sent -> Booked (listingId null = every item of the step).
    Task<int> MarkBookedAsync(Guid tripId, string stepKey, Guid? listingId, DateTime nowUtc);
}
