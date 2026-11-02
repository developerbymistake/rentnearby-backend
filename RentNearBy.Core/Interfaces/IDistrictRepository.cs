using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface IDistrictRepository : IRepository<District>
{
    Task<IEnumerable<District>> GetAllWithCitiesAsync();
    // Same predicate as GET /listings/context: IsActive AND Boundary contains the point. Null = outside every active district.
    Task<Guid?> FindActiveContainingAsync(double latitude, double longitude);
}
