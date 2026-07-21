using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface IServiceRepository : IRepository<Service>
{
    // serviceCategoryId == null returns every service.
    Task<IEnumerable<Service>> GetByServiceCategoryIdAsync(Guid? serviceCategoryId);

    // For ServiceDetailDto assembly: pulls the category (for FormType resolution) and packages together.
    Task<Service?> GetByIdWithDetailsAsync(Guid id);

    // Rail preview: active services under the given Category (categories are the rails now),
    // featured first then SortOrder, capped server-side — the client never has to fetch the whole
    // catalog just to slice out a short preview.
    Task<IEnumerable<Service>> GetPreviewByServiceCategoryIdAsync(Guid serviceCategoryId, int limit);
}
