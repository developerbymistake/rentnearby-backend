namespace RentNearBy.Core.Models.Social;

// Owner orders 2026-11-27 (Buddy_resume/post_notify/SPEC.md + DECISIONS.md): who hears about a new public trip, when, and the
// abuse caps around publishing. Pure rules; the SQL lives in PublicTripService / PostNotifier / NearbyDigestJob.
public static class PostNotifyRules
{
    // true = followers get the push at publish (owner: "do not make followers wait till morning"). false = their bell row is still
    // written at publish and the push joins the next 07:00 IST digest. One flag, no other code change.
    public static readonly bool FollowersImmediate = true;

    // A post that pushed anyone (followers and/or the nearby toggle) is a "notified post": at most 2 per rolling 7 days, 1 while
    // the account is younger than 24 hours. Later posts in the window publish silently. Deleting a post never refunds.
    public const int NotifiedPostsPerWeek = SocialLimits.NotifiedPostsPerWeek;
    public const int NotifiedPostsFirstDay = SocialLimits.NotifiedPostsFirstDay;

    // First publish or re-publish of a post: at most 4 per rolling 7 days per user (any plan); never refunded.
    public const int PublishesPerWeek = SocialLimits.PublishesPerWeek;

    // Pending (Waiting) join requests one post accepts; the next one is refused with TripRequestsFull.
    public const int PendingRequestsMax = SocialLimits.PendingRequestsMax;

    public const int NearbyKm = SocialLimits.NearMeKm;
    public const int NearbyPerPostMax = SocialLimits.NearbyPerPostMax;
    public const int NearbyStartWithinDays = SocialLimits.NearbyStartWithinDays;
    public const int NearbyPostsPerRun = 5000;
    public const int PushBatch = 1000;

    public static readonly TimeSpan Window = TimeSpan.FromDays(7);
    public static readonly TimeSpan NewAccount = TimeSpan.FromHours(24);
    public static readonly TimeSpan BumpAfter = TimeSpan.FromDays(7);
    public static readonly TimeSpan AreaStaleAfter = TimeSpan.FromDays(30);
    public static readonly TimeSpan AreaRefreshAfter = TimeSpan.FromHours(6);
    // A nearby post the digest could not send within 7 days of publish is closed without a send (no stale "new trip").
    public static readonly TimeSpan NearbyPendingMaxAge = TimeSpan.FromDays(7);
    public static readonly TimeSpan DigestCommandTimeout = TimeSpan.FromMinutes(5);
    public static readonly TimeOnly DigestAtIst = new(7, 0);
    public static readonly TimeOnly DigestLastIst = new(21, 0);

    // The notified-post limit of this account now.
    public static int NotifyLimit(DateTime accountCreatedUtc, DateTime nowUtc)
        => nowUtc - accountCreatedUtc < NewAccount ? NotifiedPostsFirstDay : NotifiedPostsPerWeek;

    // The times still inside the rolling window, oldest first.
    public static DateTime[] InWindow(IEnumerable<DateTime>? times, DateTime nowUtc)
        => (times ?? []).Where(t => t > nowUtc - Window).OrderBy(t => t).ToArray();

    // The stored list after one more use: only the in-window times are kept, so the array stays a few entries long.
    public static DateTime[] Append(IEnumerable<DateTime>? times, DateTime nowUtc) => [.. InWindow(times, nowUtc), nowUtc];

    // When one more use is allowed (null = now).
    public static DateTime? NextAllowedAt(IReadOnlyList<DateTime> inWindow, int limit)
    {
        if (inWindow.Count < limit) return null;
        if (limit <= 0) return null;
        return DateTime.SpecifyKind(inWindow[inWindow.Count - limit] + Window, DateTimeKind.Utc);
    }

    // A nearby notice only for trips that start within 60 days (a "within N months" window starts at publish: always soon).
    public static bool StartsSoonEnough(DateOnly? startDate, DateOnly todayIst)
        => startDate is not { } d || d <= todayIst.AddDays(NearbyStartWithinDays);

    // The answer to the poster's "Notify travellers near me" toggle at the publish that notifies.
    public static string NearbyState(bool requested, bool canNotify, bool posterPrivate, DateOnly? startDate, DateOnly todayIst)
    {
        if (!requested) return PostNotifyStates.Off;
        if (posterPrivate) return PostNotifyStates.Private;
        if (!StartsSoonEnough(startDate, todayIst)) return PostNotifyStates.TooFar;
        return canNotify ? PostNotifyStates.Queued : PostNotifyStates.WeekLimit;
    }

    // Safety (review 2026-11-27): a Men only / Women only post reaches nearby strangers only when the poster is of that gender
    // himself (the join rule applied to the poster), so a man can never use a "Women only" post to push his name to the women
    // around him. Anyone posts are always fine. Followers are not affected (they chose to follow him; the gender rule still
    // filters them).
    public static bool PosterFitsAudience(string joinGender, string? posterGender) => SocialRules.CanJoin(joinGender, posterGender);

    // A re-publish keeps the first PublishedAt (feed order) unless 7 days passed, so unpublish/publish never bumps a post.
    public static DateTime PublishedAtFor(DateTime? current, DateTime nowUtc)
        => current is { } c && nowUtc - c < BumpAfter ? c : nowUtc;

    // The last area sync is recent enough for the nearby digest.
    public static bool AreaFresh(DateTime? lastDistrictAt, DateTime nowUtc) => lastDistrictAt is { } at && nowUtc - at <= AreaStaleAfter;

    // PUT /social/me/area writes only when the district changed or the stored one is older than 6 hours.
    public static bool AreaNeedsWrite(Guid? storedDistrictId, DateTime? storedAt, Guid districtId, DateTime nowUtc)
        => storedDistrictId != districtId || storedAt is not { } at || nowUtc - at >= AreaRefreshAfter;

    // Posts published before today's 07:00 IST go in today's digest; later ones wait for tomorrow.
    public static DateTime DigestCutoffUtc(DateOnly todayIst) => IstTime.AtUtc(todayIst, DigestAtIst.Hour, DigestAtIst.Minute);

    // The digest sends from 07:00 IST; a missed run catches up until 21:00 IST, later it waits for the next morning.
    public static bool DigestMaySend(DateTime nowUtc)
    {
        var t = IstTime.TimeOfDayIst(nowUtc);
        return t >= DigestAtIst.ToTimeSpan() && t < DigestLastIst.ToTimeSpan();
    }
}

// Wire values of PostNotifyDto.followers / .nearby (lower camel, like MyJoinStates).
public static class PostNotifyStates
{
    // followers
    public const string Sent = "sent";
    public const string NoFollowers = "none";
    // both
    public const string WeekLimit = "weekLimit";
    public const string AlreadySent = "alreadySent";
    // nearby
    public const string Off = "off";
    public const string Queued = "queued";
    public const string TooFar = "tooFar";
    public const string Private = "private";
    public const string Gender = "gender";
}
