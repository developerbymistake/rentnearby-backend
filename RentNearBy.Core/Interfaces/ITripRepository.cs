using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface ITripRepository : IRepository<Trip>
{
    // Tracked (Update/Delete).
    Task<Trip?> GetByIdForOwnerAsync(Guid id, Guid ownerUserId);
    // AsNoTracking, UpdatedAt DESC, Take(pageSize + 1).
    Task<(IReadOnlyList<Trip> Items, bool HasMore)> GetByOwnerPagedAsync(Guid ownerUserId, int page, int pageSize);
}
