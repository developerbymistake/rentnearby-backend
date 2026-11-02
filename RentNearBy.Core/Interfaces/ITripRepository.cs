using RentNearBy.Core.Entities;
using RentNearBy.Core.Models;

namespace RentNearBy.Core.Interfaces;

public interface ITripRepository : IRepository<Trip>
{
    // Tracked (Update/Delete).
    Task<Trip?> GetByIdForOwnerAsync(Guid id, Guid ownerUserId);
    // AsNoTracking, UpdatedAt DESC, Take(pageSize + 1).
    Task<(IReadOnlyList<Trip> Items, bool HasMore)> GetByOwnerPagedAsync(Guid ownerUserId, int page, int pageSize);
    // AsNoTracking; the caller's most recently updated trip for this Atlas city, as a city snapshot. Null when none.
    Task<TripCityInfo?> FindCitySnapshotAsync(Guid ownerUserId, long cityKey);
}
