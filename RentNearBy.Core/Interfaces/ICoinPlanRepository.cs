using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface ICoinPlanRepository : IRepository<CoinPlan>
{
    Task<CoinPlan?> GetByFeatureKeyAndPlanTypeAsync(string featureKey, string planType);

    // Rows for exactly one feature — never call the inherited GetAllAsync() from a handler, it
    // returns every feature's plans mixed together now that they share one table.
    Task<IEnumerable<CoinPlan>> GetByFeatureKeyAsync(string featureKey);
}
