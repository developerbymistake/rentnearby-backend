using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface IServiceRepository : IRepository<Service>
{
    // serviceCategoryId == null returns every service.
    Task<IEnumerable<Service>> GetByServiceCategoryIdAsync(Guid? serviceCategoryId);

    // For ServiceDetailDto assembly: pulls the category (for section resolution) and packages together.
    Task<Service?> GetByIdWithDetailsAsync(Guid id);
}
