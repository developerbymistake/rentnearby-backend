using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface IInclusionRepository : IRepository<Inclusion>
{
    Task<IEnumerable<Inclusion>> GetAllOrderedAsync();
}
