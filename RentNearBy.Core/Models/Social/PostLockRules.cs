using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Models.Social;

// The trip fields the 72-hour post lock reads (read under the trip row lock by the delete and unpublish paths).
public sealed record PostLockState(
    bool IsPublic, DateTime? PublishedAt, DateTime? LastPublishedAt, string Status, DateOnly? StartDate, DateOnly? EndDate, short? PostWindowMonths);

// A refusal of the lock: the error type and its data ({unlocksAt} or {count}).
public sealed record PostLockRefusal(string ErrorType, object Data);

// Owner idea 2026-11-27 (Buddy_resume/post_notify/SPEC.md "72-HOUR POST LOCK", use cases 39-44):
// - a published post cannot be deleted or made private for 72 hours after its latest publish (edit stays allowed);
// - a trip that was ever published can be deleted only when the host is the only joined traveller, even after it was made
//   private (otherwise unpublish-then-delete would get round the rule);
// - a trip that is over (last day passed, completed after it started, or an undated window that expired) is exempt from the
//   alone rule, but NOT from the 72 hours (safety review 2): the trip dates are owner-editable (a trip save accepts any date
//   from year 2000), so "over" alone would let post -> move the dates into the past -> delete at once. An honest trip that
//   ended within 72 hours of posting waits at most those 72 hours.
// Admin removal and account deletion never go through the owner paths, so they are exempt by construction.
public static class PostLockRules
{
    public const int LockHours = SocialLimits.PostLockHours;
    public static readonly TimeSpan Lock = TimeSpan.FromHours(LockHours);

    public static PostLockState Of(Trip t)
        => new(t.IsPublic, t.PublishedAt, t.LastPublishedAt, t.Status, t.StartDate, t.EndDate, t.PostWindowMonths);

    // The start of the lock window: the latest publish, else the first one (rows from before LastPublishedAt existed).
    public static DateTime? LockStart(DateTime? lastPublishedAt, DateTime? publishedAt) => lastPublishedAt ?? publishedAt;

    // The trip is over: no lock and no "remove the others first" rule any more. Completed counts only for a trip that has
    // started: the owner can complete a trip early (TripCompleteRules: every step settled, or no dates), so Completed alone
    // would open post -> complete -> delete. An undated trip is over when its window expired.
    public static bool TripOver(PostLockState s, DateTime nowUtc)
    {
        var today = IstTime.TodayIst(nowUtc);
        if (s.Status == TripStatuses.Completed && s.StartDate is { } started && started <= today) return true;
        if ((s.EndDate ?? s.StartDate) is { } last) return last < today;
        return s.PostWindowMonths is { } months && s.PublishedAt is { } at && at.AddMonths(months) < nowUtc;
    }

    // When the lock ends; null = not locked now (never published or 72 hours passed). An over trip is still locked (see above).
    public static DateTime? UnlocksAt(PostLockState s, DateTime nowUtc)
    {
        if (LockStart(s.LastPublishedAt, s.PublishedAt) is not { } start) return null;
        var unlocks = DateTime.SpecifyKind(start, DateTimeKind.Utc) + Lock;
        return nowUtc < unlocks ? unlocks : null;
    }

    // Delete needs the host alone: the trip was ever published and is not over.
    public static bool DeleteNeedsAlone(PostLockState s, DateTime nowUtc) => s.PublishedAt is not null && !TripOver(s, nowUtc);

    // Owner delete: PostLocked inside 72 hours (also for an over trip), then RemoveMembersFirst while other travellers are
    // joined and the trip is not over. null = allowed.
    public static PostLockRefusal? DeleteRefusal(PostLockState s, int otherMembers, DateTime nowUtc)
    {
        if (s.PublishedAt is null) return null;
        if (UnlocksAt(s, nowUtc) is { } unlocksAt) return new PostLockRefusal(SocialErrorTypes.PostLocked, new { unlocksAt });
        if (!DeleteNeedsAlone(s, nowUtc)) return null;
        return otherMembers > 0 ? new PostLockRefusal(SocialErrorTypes.RemoveMembersFirst, new { count = otherMembers }) : null;
    }

    // A date edit that would turn a running published trip into an "over" one while other travellers are joined (main session
    // decision 2026-11-27, closes the gap where moving the dates into the past skipped the alone rule). Dates in the future,
    // a trip that is already over, a trip that was never published or a host who is alone are not affected.
    public static PostLockRefusal? DateMoveRefusal(PostLockState before, int otherMembers, DateOnly? newStart, DateOnly? newEnd, DateTime nowUtc)
    {
        if (before.PublishedAt is null || otherMembers <= 0 || TripOver(before, nowUtc)) return null;
        var today = IstTime.TodayIst(nowUtc);
        return (newEnd ?? newStart) is { } last && last < today
            ? new PostLockRefusal(SocialErrorTypes.TripDatesLocked, new { count = otherMembers })
            : null;
    }

    // Owner unpublish ("Make private"): only the 72-hour lock; the trip and its members stay. A private trip has nothing to lock.
    public static PostLockRefusal? UnpublishRefusal(PostLockState s, DateTime nowUtc)
        => s.IsPublic && UnlocksAt(s, nowUtc) is { } unlocksAt ? new PostLockRefusal(SocialErrorTypes.PostLocked, new { unlocksAt }) : null;
}
