using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

// Admin catalog CRUD only. Hot paths read IPlanCatalogCache, never this repository.
public interface IPlanRepository : IRepository<Plan>
{
    Task<List<Plan>> GetAllWithDetailsAsync(string? featureKey);
    Task<Plan?> GetWithDetailsAsync(Guid id);
    Task<bool> CodeExistsAsync(string featureKey, string code, Guid? exceptId);
    // Replaces the plan's features and prices as a set (tracked; caller saves).
    void ReplaceDetails(Plan plan, IEnumerable<PlanFeature> features, IEnumerable<PlanPrice> prices);
    Task<int> CountActiveHoldersAsync(Guid planId);
}
