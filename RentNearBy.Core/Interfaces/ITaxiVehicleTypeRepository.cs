using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface ITaxiVehicleTypeRepository : IRepository<TaxiVehicleType>
{
    Task<IReadOnlyList<TaxiVehicleType>> GetActiveAsync();
}
