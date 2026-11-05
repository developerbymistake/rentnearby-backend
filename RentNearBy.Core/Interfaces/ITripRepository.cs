using RentNearBy.Core.Entities;
using RentNearBy.Core.Models;

namespace RentNearBy.Core.Interfaces;

public interface ITripRepository : IRepository<Trip>
{
    // Tracked (Update/Delete).
    Task<Trip?> GetByIdForOwnerAsync(Guid id, Guid ownerUserId);
    // AsNoTracking, UpdatedAt DESC, Take(pageSize + 1).
    Task<(IReadOnlyList<Trip> Items, bool HasMore)> GetByOwnerPagedAsync(Guid ownerUserId, int page, int pageSize);
    // AsNoTracking, UpdatedAt DESC; completed picks Status = Completed, otherwise every other status.
    Task<(IReadOnlyList<Trip> Items, bool HasMore)> GetByOwnerPagedAsync(Guid ownerUserId, int page, int pageSize, bool completed);
    Task<(int Upcoming, int Completed)> GetStateCountsAsync(Guid ownerUserId);
    // AsNoTracking; Planned, not muted, StartDate in [startFrom, startTo] or EndDate in [endFrom, endTo].
    Task<IReadOnlyList<Trip>> GetReminderCandidatesAsync(DateOnly startFrom, DateOnly startTo, DateOnly endFrom, DateOnly endTo, int take);
    // AsNoTracking; Planned with EndDate <= endBefore.
    Task<IReadOnlyList<Trip>> GetAutoCompleteCandidatesAsync(DateOnly endBefore, int take);
    // Set-based, WHERE Status = 'Planned'. Returns rows affected.
    Task<int> TryCompleteAsync(Guid id, DateTime nowUtc);
    // AsNoTracking; the caller's most recently updated trip for this Atlas city, as a city snapshot. Null when none.
    Task<TripCityInfo?> FindCitySnapshotAsync(Guid ownerUserId, long cityKey);
    // FindCitySnapshotAsync, else the matching leg of the caller's most recently updated trip whose plan holds the city.
    Task<TripCityInfo?> FindLegSnapshotAsync(Guid ownerUserId, long cityKey);
    // Tracked; the row an earlier create with this client id inserted. Null when none.
    Task<Trip?> FindByClientIdAsync(Guid ownerUserId, Guid clientTripId);
}
