using RentNearBy.Core.DTOs.Buddy;

namespace RentNearBy.Core.Interfaces.Buddy;

public interface ITripSharingHooks
{
    Task<TripSharingBlockDto?> GetSharingAsync(Guid tripId, Guid ownerId, CancellationToken ct = default);

    // After commit, never throws. changedDay = the one day whose places changed, when the save touched only that day.
    Task OnPlanSavedAsync(Guid tripId, Guid ownerId, int? changedDay = null, CancellationToken ct = default);

    // Inside the delete transaction; returns viewers told.
    Task<int> OnTripDeletingAsync(Guid tripId, Guid ownerId, CancellationToken ct = default);
}
