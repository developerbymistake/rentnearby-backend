using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface IUserBlockRepository : IRepository<UserBlock>
{
    Task<bool> ExistsAsync(Guid blockerId, Guid blockedId);
}
