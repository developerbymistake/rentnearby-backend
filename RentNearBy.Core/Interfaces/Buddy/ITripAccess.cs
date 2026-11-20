using RentNearBy.Core.Models.Buddy;

namespace RentNearBy.Core.Interfaces.Buddy;

public interface ITripAccess
{
    // ONE statement: Trips JOIN owner Users LEFT JOIN TripMembers (TripId, UserId). null = trip missing, or the caller is
    // neither the owner nor Joined/Left/Removed. Callers answer 404 TripNotFound.
    Task<TripAccessInfo?> GetAsync(Guid tripId, Guid userId, CancellationToken ct = default);

    // ONE statement: owner row UNION ALL member rows (Joined; plus Left and Removed when includeFormer) JOIN Users.
    // Canonical order: owner first, then JoinedAt ascending, then UserId ascending.
    Task<IReadOnlyList<TripTraveller>> GetTravellersAsync(Guid tripId, bool includeFormer, CancellationToken ct = default);

    // ONE statement for many trips (former included), same order per trip.
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<TripTraveller>>> GetTravellersManyAsync(
        IReadOnlyCollection<Guid> tripIds, CancellationToken ct = default);
}
