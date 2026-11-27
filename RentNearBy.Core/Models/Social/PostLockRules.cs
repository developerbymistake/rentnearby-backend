using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Models.Social;

// The trip fields the delete rule reads (read under the trip row lock by the delete path).
public sealed record PostLockState(
    bool IsPublic, DateTime? PublishedAt, DateTime? LastPublishedAt, string Status, DateOnly? StartDate, DateOnly? EndDate, short? PostWindowMonths);

// A refusal: the error type and its data ({count}).
public sealed record PostLockRefusal(string ErrorType, object Data);

// Owner order 2026-11-28 (I-3): no post lock. A trip with joined members cannot be deleted until they are removed or have left
// (ANY trip, normal or ever published, over or not). The date-move rule keeps its first scope: an ever-published trip with other
// travellers cannot have its dates moved into the past while it is running.
// Admin removal and account deletion never go through the owner paths, so they are exempt by construction.
public static class PostLockRules
{
    public static PostLockState Of(Trip t)
        => new(t.IsPublic, t.PublishedAt, t.LastPublishedAt, t.Status, t.StartDate, t.EndDate, t.PostWindowMonths);

    // The trip is over (read by the date-move rule). Completed counts only for a trip that has started: the owner can complete a
    // trip early (TripCompleteRules: every step settled, or no dates). An undated post is over when its window expired.
    public static bool TripOver(PostLockState s, DateTime nowUtc)
    {
        var today = IstTime.TodayIst(nowUtc);
        if (s.Status == TripStatuses.Completed && s.StartDate is { } started && started <= today) return true;
        if ((s.EndDate ?? s.StartDate) is { } last) return last < today;
        return s.PostWindowMonths is { } months && s.PublishedAt is { } at && at.AddMonths(months) < nowUtc;
    }

    // No post lock any more: always null (kept for the DTO field PostUnlocksAt and its readers).
    public static DateTime? UnlocksAt(PostLockState s, DateTime nowUtc) => null;

    // Delete always needs the host alone (every trip, no "over" exemption).
    public static bool DeleteNeedsAlone(PostLockState s, DateTime nowUtc) => true;

    // Owner delete: RemoveMembersFirst {count} while other travellers are joined. null = allowed.
    public static PostLockRefusal? DeleteRefusal(PostLockState s, int otherMembers, DateTime nowUtc)
        => otherMembers > 0 ? new PostLockRefusal(SocialErrorTypes.RemoveMembersFirst, new { count = otherMembers }) : null;

    // A date edit that would turn a running published trip into an "over" one while other travellers are joined (main session
    // decision 2026-11-27). Dates in the future, a trip that is already over, a trip that was never published or a host who is
    // alone are not affected.
    public static PostLockRefusal? DateMoveRefusal(PostLockState before, int otherMembers, DateOnly? newStart, DateOnly? newEnd, DateTime nowUtc)
    {
        if (before.PublishedAt is null || otherMembers <= 0 || TripOver(before, nowUtc)) return null;
        var today = IstTime.TodayIst(nowUtc);
        return (newEnd ?? newStart) is { } last && last < today
            ? new PostLockRefusal(SocialErrorTypes.TripDatesLocked, new { count = otherMembers })
            : null;
    }
}
