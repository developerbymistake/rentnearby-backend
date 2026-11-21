using RentNearBy.Core.Models;

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
    int FreeDays,
    int Photos = 2,
    int RoomTypes = 2,
    int Routes = 1,
    int RingWeight = 1,
    int CalendarEntries = 4,
    int BlockedRanges = 1,
    int UpcomingDays = 15,
    int MonthsAhead = 0,
    bool MonthSummary = false,
    bool Reminders = false,
    int SpecialPrices = 1,
    bool DemandCount = false)
{
    // Every limit refusal sends this; false while payment is OFF and for anyone who already holds a plan.
    public bool CanUpgrade => PaymentEnabled && !IsPaid;

    public int Get(string key) => key switch
    {
        PlanFeatureKeys.ListingLimit => ListingLimit,
        PlanFeatureKeys.Priority => PriorityRank,
        PlanFeatureKeys.Featured => IsFeatured ? 1 : 0,
        PlanFeatureKeys.JobsPosts => JobsPosts,
        PlanFeatureKeys.Photos => Photos,
        PlanFeatureKeys.RoomTypes => RoomTypes,
        PlanFeatureKeys.Routes => Routes,
        PlanFeatureKeys.RingWeight => RingWeight,
        PlanFeatureKeys.CalendarEntries => CalendarEntries,
        PlanFeatureKeys.BlockedRanges => BlockedRanges,
        PlanFeatureKeys.UpcomingDays => UpcomingDays,
        PlanFeatureKeys.MonthsAhead => MonthsAhead,
        PlanFeatureKeys.MonthSummary => MonthSummary ? 1 : 0,
        PlanFeatureKeys.Reminders => Reminders ? 1 : 0,
        PlanFeatureKeys.SpecialPrices => SpecialPrices,
        PlanFeatureKeys.DemandCount => DemandCount ? 1 : 0,
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, "Unknown plan feature key"),
    };
}

// The one seam that reads limits. Callers never compare plan names or codes.
public interface IPlanEntitlementService
{
    // One indexed UserPlans query + cached catalog + cached payment flag.
    Task<PlanLimits> GetLimitsAsync(Guid userId, string featureKey);

    // One query for many users (admin lists).
    Task<IReadOnlyDictionary<Guid, PlanLimits>> GetLimitsBatchAsync(IReadOnlyCollection<Guid> userIds, string featureKey);
}
