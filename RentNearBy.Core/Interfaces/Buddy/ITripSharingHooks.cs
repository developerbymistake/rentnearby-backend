using RentNearBy.Core.DTOs.Buddy;

namespace RentNearBy.Core.Interfaces.Buddy;

public interface ITripSharingHooks
{
    Task<TripSharingBlockDto?> GetSharingAsync(Guid tripId, Guid ownerId, CancellationToken ct = default);

    // A trip created in this request: no member or expense can exist yet, so only the people statement runs (FB-01).
    Task<TripSharingBlockDto?> GetNewTripSharingAsync(Guid tripId, Guid ownerId, CancellationToken ct = default);

    // After commit, never throws. changedDay = the one day whose places changed, when the save touched only that day.
    Task OnPlanSavedAsync(Guid tripId, Guid ownerId, int? changedDay = null, CancellationToken ct = default);

    // Inside the delete transaction; returns viewers told.
    Task<int> OnTripDeletingAsync(Guid tripId, Guid ownerId, CancellationToken ct = default);

    // After the delete commit, never throws: live hints to the people whose join requests the delete ended.
    Task AfterTripDeletedAsync(CancellationToken ct = default);
}
