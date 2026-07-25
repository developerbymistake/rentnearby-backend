using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface ICreditPlanRepository : IRepository<CreditPlan>
{
    Task<CreditPlan?> GetByFeatureKeyAndPlanTypeAsync(string featureKey, string planType);

    // Rows for exactly one feature — never call the inherited GetAllAsync() from a handler, it
    // returns every feature's plans mixed together now that they share one table.
    Task<IEnumerable<CreditPlan>> GetByFeatureKeyAsync(string featureKey);
}
