using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface IServiceRepository : IRepository<Service>
{
    // serviceCategoryId == null returns every service.
    Task<IEnumerable<Service>> GetByServiceCategoryIdAsync(Guid? serviceCategoryId);

    // For ServiceDetailDto assembly: pulls the category (for FormType/CategorySlug resolution),
    // packages, and inclusions together.
    Task<Service?> GetByIdWithDetailsAsync(Guid id);

    // Public (no-auth) share-link/QR resolver: Service.Slug is unique only per-category (see
    // AdminCreateService's comment), so both slugs are required together, mirroring the Website's
    // own /services/{categorySlug}/{serviceSlug} URL shape.
    Task<Service?> GetBySlugWithDetailsAsync(string categorySlug, string serviceSlug);

    // Rail preview: active services under the given Category (categories are the rails now),
    // featured first then SortOrder, capped server-side — the client never has to fetch the whole
    // catalog just to slice out a short preview.
    Task<IEnumerable<Service>> GetPreviewByServiceCategoryIdAsync(Guid serviceCategoryId, int limit);
}
