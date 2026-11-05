namespace RentNearBy.Core.Interfaces;

// What a user may do for one feature right now. IsPaid is false whenever payment is off.
public sealed record PlanLimits(
    Guid PlanId,
    string PlanName,
    bool IsDefaultPlan,
    Guid? UserPlanId,
    bool IsPaid,
    string? Source,
    DateTime? StartsAt,
    DateTime? EndsAt,
    int ListingLimit,
    short PriorityRank,
    bool IsFeatured,
    int JobsPosts,
    bool PaymentEnabled,
    int FreeDays);

// The one seam that reads limits. Callers never compare plan names or codes.
public interface IPlanEntitlementService
{
    // One indexed UserPlans query + cached catalog + cached payment flag.
    Task<PlanLimits> GetLimitsAsync(Guid userId, string featureKey);

    // One query for many users (admin lists).
    Task<IReadOnlyDictionary<Guid, PlanLimits>> GetLimitsBatchAsync(IReadOnlyCollection<Guid> userIds, string featureKey);
}
