using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface IServiceCategoryRepository : IRepository<ServiceCategory>
{
    // Categories ARE the catalog's top level (one consumer rail per active row) — the full
    // ordered list is the only shape anyone needs.
    Task<IEnumerable<ServiceCategory>> GetAllOrderedAsync();
}
