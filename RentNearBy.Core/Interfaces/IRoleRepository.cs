using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface IRoleRepository : IRepository<Role>
{
    Task<IReadOnlyList<Role>> GetActiveOrderedAsync();
    Task<Role?> GetActiveByKeyAsync(string key);
}
