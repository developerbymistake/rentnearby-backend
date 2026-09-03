using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface IAppTabRepository : IRepository<AppTab>
{
    Task<AppTab?> GetByKeyAsync(string tabKey);
}
