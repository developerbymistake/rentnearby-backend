namespace RentNearBy.Core.Models;

// City road matrix (trip:matrix:{key}): the build gate, the "never buildable" marker and the build generation.
public static class TripMatrixRules
{
    public static string GenKey(long cityKey) => $"trip:matrix:{cityKey}:gen";
    public static string SkipKey(long cityKey) => $"trip:matrix:{cityKey}:skip";

    public static bool IsBuildable(int distinctSpots, int maxSpots) => distinctSpots >= 2 && distinctSpots <= maxSpots;

    // How long "this city has no matrix" holds: a Ready list as long as a matrix would, a NotReady or stale list until the
    // spot cache asks Atlas again. null = buildable, or no spot list at all (Atlas down): the next plan tries again.
    public static TimeSpan? SkipTtl(string? spotStatus, int distinctSpots, TripPlannerOptions o)
    {
        if (spotStatus == null || IsBuildable(distinctSpots, o.CityMatrixMaxSpots)) return null;
        return spotStatus == TripSpotStatuses.Ready
            ? TimeSpan.FromHours(o.CacheTtlHours)
            : TimeSpan.FromMinutes(o.NotReadyTtlMinutes);
    }

    // before = generation read ahead of the spot list ("" when none); now = null when Redis failed.
    public static bool SameGeneration(string before, string? now) => now != null && string.Equals(before, now, StringComparison.Ordinal);
}
