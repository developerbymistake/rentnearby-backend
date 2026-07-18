using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface IServiceRepository : IRepository<Service>
{
    // serviceCategoryId == null returns every service.
    Task<IEnumerable<Service>> GetByServiceCategoryIdAsync(Guid? serviceCategoryId);

    // For ServiceDetailDto assembly: pulls the category (for section resolution) and packages together.
    Task<Service?> GetByIdWithDetailsAsync(Guid id);

    // Home-rail preview: active services under any category of the given Section, featured first
    // then SortOrder, capped server-side — the client never has to fetch the whole catalog just to
    // slice out a short preview.
    Task<IEnumerable<Service>> GetPreviewByServiceSectionIdAsync(Guid serviceSectionId, int limit);
}
