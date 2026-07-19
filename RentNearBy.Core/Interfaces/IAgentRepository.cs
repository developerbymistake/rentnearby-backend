using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface IAgentRepository : IRepository<Agent>
{
    // Both Include AgentServiceCategories.ServiceCategory (for AgentDto's flattened category ids/names)
    // and User (for AgentDto's flattened UserName/UserPhoneNumber).
    Task<IEnumerable<Agent>> GetAllWithCategoriesAsync();
    Task<Agent?> GetByIdWithCategoriesAsync(Guid id);

    // Category-scoped agent picker for admin's inquiry-assign flow (only agents serving the Inquiry's
    // Service's category are eligible).
    Task<IEnumerable<Agent>> GetActiveByServiceCategoryIdAsync(Guid serviceCategoryId);

    // Resolves the caller's own Agent record from their JWT-derived UserId — the "am I an agent"
    // check for the consumer app's My Leads feature. Only an active Agent counts (deactivating an
    // Agent should revoke their My Leads access). Never look an agent up by a client-supplied id.
    Task<Agent?> GetByUserIdAsync(Guid userId);

    // Unfiltered by IsActive, unlike GetByUserIdAsync above — used only for the admin create-time
    // duplicate-link guard, which must catch an inactive agent still holding the link (the DB's
    // partial unique index doesn't care about IsActive either, so this has to match it exactly to
    // give a friendly ConflictResponse instead of a raw constraint-violation exception).
    Task<bool> ExistsByUserIdAsync(Guid userId);
}
