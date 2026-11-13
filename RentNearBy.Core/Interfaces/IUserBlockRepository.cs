using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface IUserBlockRepository : IRepository<UserBlock>
{
    Task<bool> ExistsAsync(Guid blockerId, Guid blockedId);
    Task<bool> ExistsEitherWayAsync(Guid a, Guid b);
    Task<UserBlock?> GetByBlockerAndBlockedAsync(Guid blockerId, Guid blockedId);
}
