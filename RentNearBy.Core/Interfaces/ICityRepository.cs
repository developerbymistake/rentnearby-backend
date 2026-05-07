using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface ICityRepository : IRepository<City>
{
    Task<IEnumerable<City>> GetByDistrictIdAsync(Guid districtId);
}
