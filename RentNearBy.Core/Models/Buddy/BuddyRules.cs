namespace RentNearBy.Core.Models.Buddy;

public static class BuddyRelations
{
    public const string None = "none";
    public const string RequestedByMe = "requestedByMe";
    public const string RequestedMe = "requestedMe";
    public const string Buddy = "buddy";
    public const string Cooldown = "cooldown";
    public const string Self = "self";
}

public static class SentStates
{
    public const string Waiting = "waiting";
    public const string Ended = "ended";
}

public static class BuddyTripStates
{
    public const string None = "None";
    public const string Invited = "Invited";
    public const string Joined = "Joined";
}

public sealed record RequestFacts(Guid Id, Guid FromUserId, string Status, DateTime CreatedAt, DateTime ExpiresAt, bool BlockedSender);

public sealed record RelationResult(string Relation, Guid? RequestId, DateTime? ExpiresAt);

// A service answer: Value on success, else an error type of BuddyErrorTypes (+ optional data).
public sealed record BuddyResult<T>(T? Value, string? ErrorType = null, object? ErrorData = null)
{
    public bool Ok => ErrorType is null;

    public static BuddyResult<T> Success(T value) => new(value);

    public static BuddyResult<T> Fail(string type, object? data = null) => new(default, type, data);
}

// Pure rules of the buddy lane (doc 2.0.3 to 2.0.6, contract 2.1).
public static class BuddyRules
{
    // Runs the find statement with this value when the text is not a username, so every "No one found" costs the same.
    public const string ImpossibleUsername = "Bakhli#00000000";

    public static readonly Guid UserReasonOtherId = Guid.Parse("c2000000-0000-0000-0000-000000000004");

    // Trimmed, prefix matched without letter case, stored form "Bakhli#" + 8 digits; null = not a username.
    public static string? NormalizeFind(string? query)
    {
        var text = query?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length != Usernames.Prefix.Length + Usernames.DigitCount) return null;
        if (!text.StartsWith(Usernames.Prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var stored = Usernames.Prefix + text[Usernames.Prefix.Length..];
        return Usernames.IsValid(stored) ? stored : null;
    }

    // Declined, or closed by a block of the sender: the sender still sees "waiting" until ExpiresAt.
    public static bool IsSilent(string status, bool blockedSender)
        => status == BuddyRequestStatuses.Declined || (status == BuddyRequestStatuses.ClosedByBlock && blockedSender);

    // The sender's view: an open request, or a silent one before its 14 days are up.
    public static bool IsWaitingForSender(RequestFacts r, DateTime nowUtc)
        => r.ExpiresAt > nowUtc && (r.Status == BuddyRequestStatuses.Pending || IsSilent(r.Status, r.BlockedSender));

    public static bool IsOpenPending(RequestFacts r, DateTime nowUtc)
        => r.Status == BuddyRequestStatuses.Pending && r.ExpiresAt > nowUtc;

    // L5: 30 days from CreatedAt after any end (declined, expired, cancelled, block-closed); an accepted request never cools down.
    public static bool InCooldown(RequestFacts? mine, DateTime nowUtc)
    {
        if (mine is null || IsWaitingForSender(mine, nowUtc)) return false;
        if (mine.Status is BuddyRequestStatuses.Accepted or BuddyRequestStatuses.ClosedByDeletion) return false;
        return mine.CreatedAt > nowUtc - BuddyLimits.ResendCooldown;
    }

    public static RelationResult Relation(Guid callerId, Guid personId, bool isBuddy, RequestFacts? myLatest, Guid? theirPendingId, DateTime nowUtc)
    {
        if (callerId == personId) return new(BuddyRelations.Self, null, null);
        if (isBuddy) return new(BuddyRelations.Buddy, null, null);
        if (myLatest is not null && IsWaitingForSender(myLatest, nowUtc))
            return new(BuddyRelations.RequestedByMe, myLatest.Id, myLatest.ExpiresAt);
        if (theirPendingId is { } theirs) return new(BuddyRelations.RequestedMe, theirs, null);
        if (InCooldown(myLatest, nowUtc)) return new(BuddyRelations.Cooldown, null, null);
        return new(BuddyRelations.None, null, null);
    }

    // B4: waiting while open or silent before ExpiresAt, else ended (shown 7 days after ExpiresAt).
    public static string SentState(DateTime expiresAt, DateTime nowUtc) => expiresAt > nowUtc ? SentStates.Waiting : SentStates.Ended;

    public static bool ShownInSent(string status, bool blockedSender, DateTime expiresAt, DateTime nowUtc)
        => (status is BuddyRequestStatuses.Pending or BuddyRequestStatuses.Declined or BuddyRequestStatuses.Expired
            || (status == BuddyRequestStatuses.ClosedByBlock && blockedSender))
           && expiresAt > nowUtc - BuddyLimits.EndedSentShownFor;

    // B8 forTripId: a silent decline inside its 7 days still reads Invited (no leak).
    public static string TripState(string? memberStatus, DateTime? inviteExpiresAt, DateTime? respondedAt, DateTime nowUtc) => memberStatus switch
    {
        TripMemberStatuses.Joined => BuddyTripStates.Joined,
        TripMemberStatuses.Invited when inviteExpiresAt > nowUtc => BuddyTripStates.Invited,
        TripMemberStatuses.Declined when respondedAt > nowUtc - BuddyLimits.ReinviteAfterDecline => BuddyTripStates.Invited,
        _ => BuddyTripStates.None,
    };

    // Admin hold: null clears; else in the future and at most 365 days ahead.
    public static bool IsValidHold(DateTime? until, DateTime nowUtc)
        => until is null || (until.Value > nowUtc && until.Value <= nowUtc + BuddyLimits.MaxAdminHold);

    // The report note is kept only for the reason Other.
    public static string ReportDetails(Guid reasonId, string? note)
        => reasonId == UserReasonOtherId ? (note?.Trim() ?? string.Empty) : string.Empty;
}
