using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface IDistrictRepository : IRepository<District>
{
    Task<IEnumerable<District>> GetAllWithCitiesAsync();
    // Projected list (no Boundary), same unordered set as GetAllAsync.
    Task<List<DistrictDto>> GetAllDtosAsync();
    // Same predicate as GET /listings/context: IsActive AND Boundary contains the point. Null = outside every active district.
    Task<Guid?> FindActiveContainingAsync(double latitude, double longitude);
}
