using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface IStayRegistrationTypeRepository : IRepository<StayRegistrationType>
{
    Task<IReadOnlyList<StayRegistrationType>> GetActiveAsync();
}
