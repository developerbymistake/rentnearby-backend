using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

// The On* calls run inside the caller's transaction; the caller passes their result to AfterCommitAsync once committed.
public interface ITripEnquiryClosure
{
    Task<IReadOnlyList<TripClosurePush>> OnListingsRemovedAsync(string kind, IReadOnlyCollection<Guid> listingIds);
    // Also sets TripId = null on every enquiry of the trip.
    Task<IReadOnlyList<TripClosurePush>> OnTripDeletedAsync(Guid tripId);
    Task<IReadOnlyList<TripClosurePush>> OnTravellerDeletedAsync(Guid userId);
    // No pushes.
    Task OnBlockAsync(Guid userA, Guid userB);
    // Driver cleared his local rate: open Asked/FareSent leads -> NotAvailable; booked rows untouched.
    Task<IReadOnlyList<TripClosurePush>> OnTaxiRateClearedAsync(Guid taxiId);
    // After commit: chat broadcasts of the system lines written by the last call, then the pushes. Never throws.
    Task AfterCommitAsync(IReadOnlyList<TripClosurePush> pushes);
}

public sealed record TripClosurePush(NotificationEvent Notification, string DedupeKey, Guid? TripId, Guid EnquiryId);
