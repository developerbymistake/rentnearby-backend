using RentNearBy.Core.Models.Owner;

namespace RentNearBy.Core.Models.Social;

// Admin edit of a Seeker plan (/admin/plans with featureKey Seeker): only the six Seeker keys, each inside its range,
// switches 0 or 1, and a paid plan never below the Free plan on any key.
public static class SeekerPlanValueRules
{
    public const int CountMax = 1000;

    public static readonly IReadOnlyList<string> Switches = [SeekerPlanKeys.GenderOnlyTrips, SeekerPlanKeys.NearMe];

    public static string Label(string key) => key switch
    {
        SeekerPlanKeys.ActiveTrips => "Active trips",
        SeekerPlanKeys.PublicTrips => "Public trips",
        SeekerPlanKeys.JoinRequestsPerMonth => "Join requests a month",
        SeekerPlanKeys.NewChatsPerMonth => "New people messaged a month",
        SeekerPlanKeys.GenderOnlyTrips => "Women-only trips",
        SeekerPlanKeys.NearMe => "Near me filter",
        _ => key,
    };

    public static (int Min, int Max) Range(string key) => key switch
    {
        SeekerPlanKeys.GenderOnlyTrips or SeekerPlanKeys.NearMe => (0, 1),
        SeekerPlanKeys.ActiveTrips => (1, CountMax),
        _ => (0, CountMax),
    };

    // null = valid.
    public static string? KeyError(string key, int value)
    {
        if (!SeekerPlanKeys.All.Contains(key)) return $"Unknown Seeker plan value: {key}";
        var (min, max) = Range(key);
        return value < min || value > max ? $"{Label(key)} must be between {min} and {max}" : null;
    }

    // A missing key reads as its seeded Free default, the same fallback SeekerLimitsService uses.
    public static PlanValueBelowFree? BelowFree(IReadOnlyDictionary<string, int> free, IReadOnlyDictionary<string, int> plus)
    {
        foreach (var key in SeekerPlanKeys.All)
        {
            var f = free.TryGetValue(key, out var fv) ? fv : SeekerPlanKeys.FreeDefault(key);
            var p = plus.TryGetValue(key, out var pv) ? pv : SeekerPlanKeys.FreeDefault(key);
            if (p < f) return new PlanValueBelowFree(key, f, p);
        }
        return null;
    }
}
