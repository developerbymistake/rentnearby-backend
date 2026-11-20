using RentNearBy.Core.DTOs.Buddy;

namespace RentNearBy.Core.Interfaces.Buddy;

public interface ITripSharingHooks
{
    Task<TripSharingBlockDto?> GetSharingAsync(Guid tripId, Guid ownerId, CancellationToken ct = default);

    // After commit, never throws.
    Task OnPlanSavedAsync(Guid tripId, Guid ownerId, CancellationToken ct = default);

    // Inside the delete transaction; returns viewers told.
    Task<int> OnTripDeletingAsync(Guid tripId, Guid ownerId, CancellationToken ct = default);
}
