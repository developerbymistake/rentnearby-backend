using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface IDistrictRepository : IRepository<District>
{
    Task<IEnumerable<District>> GetByCityIdAsync(Guid cityId);
}
