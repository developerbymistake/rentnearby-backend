namespace RentNearBy.Core.Interfaces.Social;

// GroupChat (owner 2026-11-27): may send in trip group chats (SeekerGroupChat switch).
public sealed record SeekerLimits(bool IsPlus, bool CanUpgrade, int ActiveTrips, int PublicTrips, int JoinRequestsPerMonth, int NewChatsPerMonth, bool GenderOnlyTrips, bool NearMe,
    bool GroupChat);

// The seeker's plan numbers (contract 4), read from the Seeker plans of the catalog.
public interface ISeekerLimits
{
    Task<SeekerLimits> GetAsync(Guid userId, CancellationToken ct = default);

    // The plan numbers alone, without the social hold (PLAN_STATES_SOCIAL): what the plan reconcile keeps active.
    Task<SeekerLimits> GetPlanAsync(Guid userId, CancellationToken ct = default) => GetAsync(userId, ct);
}
