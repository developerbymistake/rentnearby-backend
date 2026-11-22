namespace RentNearBy.Core.Models.Buddy;

public static class InviteOutcomes
{
    public const string Invited = "Invited";
    public const string AlreadyInvited = "AlreadyInvited";
    public const string AlreadyOnTrip = "AlreadyOnTrip";
    public const string NotBuddy = "NotBuddy";
}

public static class InviteOptions
{
    public const string Available = "Available";
    public const string OnTrip = "OnTrip";
    public const string InviteSent = "InviteSent";
    public const string Full = "Full";
    public const string NoPlaces = "NoPlaces";
}

public static class InviteEndedReasons
{
    public const string Cancelled = "Cancelled";
    public const string Expired = "Expired";
    public const string Declined = "Declined";
    public const string Unknown = "Unknown";
}

public static class TripSharingRoles
{
    public const string Owner = "Owner";
    public const string Viewer = "Viewer";
    public const string Former = "Former";
}

// What S2 knows about one invited id (one row of the evaluation statement).
public sealed record InviteCandidate(
    Guid UserId, bool Exists, bool IsActive, bool IsBuddy, bool Blocked,
    string? MemberStatus, DateTime? InviteExpiresAt, DateTime? RespondedAt);

// Pure rules of trip sharing (contract 2.2, doc 3.0.8 and 3.0.10).
public static class TripSharingRules
{
    public static readonly TimeSpan IstOffset = TimeSpan.FromHours(5.5);

    public static DateOnly TodayIst(DateTime nowUtc) => IstTime.TodayIst(nowUtc);

    // min(now + 14 days, end of the trip's last day IST); no dates = 14 days.
    public static DateTime InviteExpiresAt(DateTime nowUtc, DateOnly? endDate)
    {
        var byDays = nowUtc + BuddyLimits.InviteExpiry;
        if (endDate is null) return byDays;
        var endOfDayUtc = endDate.Value.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) - IstOffset - TimeSpan.FromSeconds(1);
        return endOfDayUtc < byDays ? endOfDayUtc : byDays;
    }

    public static bool IsEnded(string status, DateOnly? endDate, DateOnly todayIst)
        => status == TripStatuses.Completed || (endDate is { } end && end < todayIst);

    public static bool IsUpcoming(string status, DateOnly? endDate, DateOnly todayIst) => !IsEnded(status, endDate, todayIst);

    public static bool IsWaitingInvite(string? status, DateTime? expiresAt, DateTime nowUtc)
        => status == TripMemberStatuses.Invited && expiresAt is { } e && e > nowUtc;

    public static bool IsRecentDecline(string? status, DateTime? respondedAt, DateTime nowUtc)
        => status == TripMemberStatuses.Declined && respondedAt is { } r && r > nowUtc - BuddyLimits.ReinviteAfterDecline;

    public static int SeatsLeft(int joinedViewers, int waitingInvites)
        => Math.Max(0, BuddyLimits.MaxViewers - joinedViewers - waitingInvites);

    // NotBuddy for every "cannot" reason so the owner never learns of a block; Invited = a new invite is written.
    public static string Classify(InviteCandidate c, Guid ownerId, DateTime nowUtc)
    {
        if (c.UserId == ownerId || !c.Exists || !c.IsActive || !c.IsBuddy || c.Blocked) return InviteOutcomes.NotBuddy;
        if (c.MemberStatus == TripMemberStatuses.Joined) return InviteOutcomes.AlreadyOnTrip;
        if (IsWaitingInvite(c.MemberStatus, c.InviteExpiresAt, nowUtc) || IsRecentDecline(c.MemberStatus, c.RespondedAt, nowUtc))
            return InviteOutcomes.AlreadyInvited;
        return InviteOutcomes.Invited;
    }

    // S17: OnTrip, InviteSent, NoPlaces, Full, Available (first that holds).
    public static string Option(string? memberStatus, DateTime? inviteExpiresAt, DateTime? respondedAt, int spotCount,
        int joinedViewers, int waitingInvites, DateTime nowUtc)
    {
        if (memberStatus == TripMemberStatuses.Joined) return InviteOptions.OnTrip;
        if (IsWaitingInvite(memberStatus, inviteExpiresAt, nowUtc) || IsRecentDecline(memberStatus, respondedAt, nowUtc))
            return InviteOptions.InviteSent;
        if (spotCount <= 0) return InviteOptions.NoPlaces;
        if (SeatsLeft(joinedViewers, waitingInvites) <= 0) return InviteOptions.Full;
        return InviteOptions.Available;
    }

    // S7/S8/S9 for the caller's own invite that is not open.
    public static string EndedReason(string status, DateTime inviteExpiresAt, DateTime nowUtc) => status switch
    {
        TripMemberStatuses.Invited when inviteExpiresAt <= nowUtc => InviteEndedReasons.Expired,
        TripMemberStatuses.Expired => InviteEndedReasons.Expired,
        TripMemberStatuses.Cancelled => InviteEndedReasons.Cancelled,
        TripMemberStatuses.Declined => InviteEndedReasons.Declined,
        _ => InviteEndedReasons.Unknown,
    };

    // W/"<Trips.UpdatedAt ticks>-<balancesVersion>-<travellerCount>-<bookingStamp>[-<faceStamp hex>]".
    public static string ViewETag(DateTime tripUpdatedAt, long balancesVersion, int travellerCount, long bookingStamp, uint faceStamp = 0)
        => $"W/\"{tripUpdatedAt.Ticks}-{balancesVersion}-{travellerCount}-{bookingStamp}{(faceStamp == 0 ? "" : $"-{faceStamp:x}")}\"";

    // Stable across restarts (FNV-1a): moves when a shown person's picture changes, 0 when nobody has one.
    public static uint FaceStamp(IEnumerable<string?> photoUrls)
    {
        uint hash = 2166136261;
        var any = false;
        foreach (var url in photoUrls)
        {
            any |= !string.IsNullOrEmpty(url);
            foreach (var ch in url ?? string.Empty) hash = (hash ^ ch) * 16777619;
            hash = (hash ^ '|') * 16777619;
        }
        return !any ? 0 : hash == 0 ? 1 : hash;
    }

    public static bool ETagMatches(string? ifNoneMatch, string etag)
    {
        if (string.IsNullOrWhiteSpace(ifNoneMatch)) return false;
        return ifNoneMatch.Split(',').Select(v => v.Trim()).Any(v => v == "*" || v == etag || "W/" + v == etag);
    }

    // clamp(today IST - start + 1, 1, dayCount); 1 without dates.
    public static int DayToday(DateOnly? startDate, int dayCount, DateOnly todayIst)
    {
        if (startDate is null) return 1;
        var day = todayIst.DayNumber - startDate.Value.DayNumber + 1;
        return Math.Clamp(day, 1, Math.Max(1, dayCount));
    }

    // A viewer sees usernames only of himself, the owner and his own buddies (frame 3.14); the owner sees all.
    public static string? VisibleUsername(bool callerIsOwner, Guid callerId, Guid personId, bool personIsOwner, bool isCallersBuddy, string? username)
        => callerIsOwner || personId == callerId || personIsOwner || isCallersBuddy ? username : null;

    public static string? FirstWord(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        var space = trimmed.IndexOf(' ');
        return space < 0 ? trimmed : trimmed[..space];
    }
}
