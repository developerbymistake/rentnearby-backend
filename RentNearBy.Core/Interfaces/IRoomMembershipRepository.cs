using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface IRoomMembershipRepository
{
    Task AddAsync(RoomMembership membership);
    Task<RoomMembership?> GetActiveByUserIdAsync(Guid userId);
    Task<RoomMembership?> GetByIdAsync(Guid id);
    Task<bool> HasUsedFreePlanAsync(Guid userId);
    Task<IReadOnlyList<RoomMembership>> GetExpiredPagedAsync(DateTime beforeDate, int page, int pageSize);
    Task SaveAsync();
}
