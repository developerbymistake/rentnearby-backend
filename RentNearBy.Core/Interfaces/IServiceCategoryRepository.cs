using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface IServiceCategoryRepository : IRepository<ServiceCategory>
{
    // serviceSectionId == null returns every category (flat-route optional query-param convention,
    // matches GET /admin/cities?districtId=).
    Task<IEnumerable<ServiceCategory>> GetByServiceSectionIdAsync(Guid? serviceSectionId);
}
