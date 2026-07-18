using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface IAgentRepository : IRepository<Agent>
{
    // Both Include AgentServiceCategories.ServiceCategory, for AgentDto's flattened category ids/names.
    Task<IEnumerable<Agent>> GetAllWithCategoriesAsync();
    Task<Agent?> GetByIdWithCategoriesAsync(Guid id);

    // Category-scoped agent picker for admin's inquiry-assign flow (only agents serving the Inquiry's
    // Service's category are eligible).
    Task<IEnumerable<Agent>> GetActiveByServiceCategoryIdAsync(Guid serviceCategoryId);
}
