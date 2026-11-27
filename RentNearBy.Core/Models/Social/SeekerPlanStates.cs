namespace RentNearBy.Core.Models.Social;

// One owned trip as the seeker plan reconcile sees it (PLAN_STATES_SOCIAL 1). Open = Draft or Planned. Grandfathered = made
// before the owner finished the social onboarding (the first social release) and before any Seeker Plus started (a trip made on
// Plus is never grandfathered, even without a social profile). Fresh = the post window and start date still hold.
public sealed record SeekerPlanRow(
    Guid Id, DateTime CreatedAt, DateTime? PublishedAt, bool Open, bool Grandfathered, bool InactiveByPlan, bool IsPublic, bool PausedByPlan, bool Fresh);

// What the reconcile writes. Every list holds only rows whose stored value differs from the target, so a second run is empty.
public sealed record SeekerPlanTargets(
    IReadOnlyList<Guid> Inactivate, IReadOnlyList<Guid> Activate, IReadOnlyList<Guid> PausePosts, IReadOnlyList<Guid> RestorePosts,
    IReadOnlyList<Guid> DropPaused, int InactiveAfter, int PausedAfter)
{
    public bool Changed => Inactivate.Count + Activate.Count + PausePosts.Count + RestorePosts.Count + DropPaused.Count > 0;
}

// PLAN_STATES_SOCIAL 1 (AFTER and RESUBSCRIBE) as a pure choice. The newest activeLimit open trips that are not a post stay
// active, older ones are inactive (kept, read-only); grandfathered trips are never made inactive and do not use a place. Posts
// (public, or a fresh one paused by the plan) never use an active place (owner 2026-11-28, I-3, same rule as TripSlots): the
// newest publicLimit stay public, the others are paused; a resubscribe brings them back in the same order. Nothing is deleted.
public static class SeekerPlanChoice
{
    public static SeekerPlanTargets Choose(IReadOnlyList<SeekerPlanRow> rows, int activeLimit, int publicLimit, bool grandfather = SeekerPlanStates.Grandfathering)
    {
        var ranked = rows
            .Where(r => r.Open && !(grandfather && r.Grandfathered) && !IsPost(r))
            .OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id)
            .ToList();
        var keep = ranked.Take(Math.Max(0, activeLimit)).Select(r => r.Id).ToHashSet();
        var rankedIds = ranked.Select(r => r.Id).ToHashSet();
        bool Inactive(SeekerPlanRow r) => rankedIds.Contains(r.Id) && !keep.Contains(r.Id);

        var inactivate = rows.Where(r => Inactive(r) && !r.InactiveByPlan).Select(r => r.Id).ToList();
        var activate = rows.Where(r => !Inactive(r) && r.InactiveByPlan).Select(r => r.Id).ToList();

        var posts = rows.Where(r => r.Open && r.Fresh && (r.IsPublic || r.PausedByPlan)).ToList();
        var shown = posts
            .Where(r => !Inactive(r))
            .OrderByDescending(r => r.PublishedAt ?? DateTime.MinValue).ThenByDescending(r => r.Id)
            .Take(Math.Max(0, publicLimit))
            .Select(r => r.Id)
            .ToHashSet();
        var pause = posts.Where(r => !shown.Contains(r.Id) && (r.IsPublic || !r.PausedByPlan)).Select(r => r.Id).ToList();
        var restore = posts.Where(r => shown.Contains(r.Id) && (!r.IsPublic || r.PausedByPlan)).Select(r => r.Id).ToList();
        var postIds = posts.Select(r => r.Id).ToHashSet();
        var drop = rows.Where(r => r.PausedByPlan && !postIds.Contains(r.Id)).Select(r => r.Id).ToList();

        return new SeekerPlanTargets(inactivate, activate, pause, restore, drop,
            rows.Count(Inactive), posts.Count(r => !shown.Contains(r.Id)));
    }

    // A post (the C# twin of PublicTripReader.HeldPost): public or paused by the plan, open and still fresh. An expired or stale-paused
    // post is a trip again and takes an active place.
    private static bool IsPost(SeekerPlanRow r) => (r.IsPublic || r.PausedByPlan) && r.Open && r.Fresh;
}

public static class SeekerPlanStates
{
    // Owner decision (default, may be overruled): trips made before the first social release stay active after Plus ends.
    public const bool Grandfathering = true;

    // Writes still allowed on an inactive trip or a paused post: read verbs, delete the trip, leave it, remove or uninvite a member
    // and the money settling between travellers. Everything else is refused with TripInactiveByPlan.
    public static bool WriteAllowed(string method, string? routePattern)
    {
        if (IsRead(method)) return true;
        var p = (routePattern ?? string.Empty).TrimEnd('/');
        var delete = string.Equals(method, "DELETE", StringComparison.OrdinalIgnoreCase);
        if (delete && (p.EndsWith("/trips/{id:guid}", StringComparison.Ordinal)
                       || p.EndsWith("/members/{userId:guid}", StringComparison.Ordinal)
                       || p.EndsWith("/invites/{userId:guid}", StringComparison.Ordinal)))
            return true;
        return p.EndsWith("/leave", StringComparison.Ordinal) || p.Contains("/payments/", StringComparison.Ordinal);
    }

    private static bool IsRead(string method)
        => string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)
           || string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase)
           || string.Equals(method, "OPTIONS", StringComparison.OrdinalIgnoreCase);
}

// The one calm notice at the end of Plus (PLAN_STATES_SOCIAL 2).
public static class SeekerPlanCopy
{
    public const string EndedTitle = "Plus ended";

    public static string Ended(int active, int paused)
    {
        if (paused <= 0) return "Bakhli Plus has ended. You are on the free plan now.";
        var stay = active == 1 ? "1 trip stays active" : $"{active} trips stay active";
        var rest = paused == 1 ? "1 is paused. Buy Plus to restore it." : $"{paused} are paused. Buy Plus to restore them.";
        return $"Plus ended. {stay}, {rest}";
    }
}
