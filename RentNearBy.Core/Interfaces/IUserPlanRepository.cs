using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface IUserPlanRepository : IRepository<UserPlan>
{
    // Active and not past EndsAt (ix_userplans_active_per_feature).
    Task<UserPlan?> GetActiveAsync(Guid userId, string featureKey);
    Task<List<UserPlan>> GetHistoryAsync(Guid userId, int limit);
    Task<bool> HasAnyAsync(Guid userId, string featureKey);
}
