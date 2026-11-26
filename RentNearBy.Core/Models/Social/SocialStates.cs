namespace RentNearBy.Core.Models.Social;

internal static class SocialSqlList
{
    public static string List(IEnumerable<string> values) => string.Join(", ", values.Select(v => $"'{v}'"));
}

public static class SocialGenders
{
    public const string Male = "Male";
    public const string Female = "Female";
    public const string Other = "Other";

    public static readonly IReadOnlyList<string> All = [Male, Female, Other];
    public static readonly string SqlList = SocialSqlList.List(All);
}

// Trips.JoinGender: A anyone, M men only, F women only.
public static class JoinGenders
{
    public const string Anyone = "A";
    public const string Men = "M";
    public const string Women = "F";

    public static readonly IReadOnlyList<string> All = [Anyone, Men, Women];
    public static readonly string SqlList = SocialSqlList.List(All);

    // Feed filter `who` (spec 22): any (default, every post) | anyone | women | men.
    public const string WhoAny = "any";
    public const string WhoAnyone = "anyone";
    public const string WhoWomen = "women";
    public const string WhoMen = "men";

    // true = a known value; code = the join rule to match, null = every post.
    public static bool TryWhoFilter(string? who, out string? code)
    {
        code = who switch
        {
            WhoAnyone => Anyone,
            WhoWomen => Women,
            WhoMen => Men,
            _ => null,
        };
        return who is null or WhoAny || code is not null;
    }
}

public static class FollowStatuses
{
    public const string Accepted = "A";
    public const string Requested = "R";

    public static readonly IReadOnlyList<string> All = [Accepted, Requested];
    public static readonly string SqlList = SocialSqlList.List(All);
}

// Wire values of the follow state answers.
public static class FollowStates
{
    public const string Following = "following";
    public const string Requested = "requested";
    public const string None = "none";
    public const string Accepted = "accepted";
}

public static class MessagePolicies
{
    public const string Everyone = "Everyone";
    public const string Following = "Following";
    public const string Buddies = "Buddies";

    public static readonly IReadOnlyList<string> All = [Everyone, Following, Buddies];
    public static readonly string SqlList = SocialSqlList.List(All);
}

public static class JoinRequestStatuses
{
    public const string Waiting = "Waiting";
    public const string Accepted = "Accepted";
    public const string Declined = "Declined";
    public const string Cancelled = "Cancelled";
    public const string Expired = "Expired";
    public const string ClosedByBlock = "ClosedByBlock";

    public static readonly IReadOnlyList<string> All = [Waiting, Accepted, Declined, Cancelled, Expired, ClosedByBlock];
    public static readonly string SqlList = SocialSqlList.List(All);
}

// PublicTrip.myJoin.
public static class MyJoinStates
{
    public const string None = "none";
    public const string Requested = "requested";
    public const string Member = "member";
    public const string Declined = "declined";
}

public static class TripJoinedVia
{
    public const string Invite = "Invite";
    public const string Request = "Request";

    public static readonly IReadOnlyList<string> All = [Invite, Request];
    public static readonly string SqlList = SocialSqlList.List(All);
}

public static class ChatKinds
{
    public const string Direct = "D";
    public const string Trip = "T";

    public static readonly IReadOnlyList<string> All = [Direct, Trip];
    public static readonly string SqlList = SocialSqlList.List(All);
}

public static class ChatRoomStatuses
{
    public const string Request = "Request";
    public const string Active = "Active";
    public const string Declined = "Declined";

    public static readonly IReadOnlyList<string> All = [Request, Active, Declined];
    public static readonly string SqlList = SocialSqlList.List(All);
}

// ChatRoomMembers.ListBucket.
public static class ChatBuckets
{
    public const string People = "P";
    public const string Trips = "T";
    public const string Requests = "R";
    public const string Hidden = "X";

    public static readonly IReadOnlyList<string> All = [People, Trips, Requests, Hidden];
    public static readonly string SqlList = SocialSqlList.List(All);

    // Query value of GET /social/chat/rooms?bucket=.
    public static string? FromQuery(string? bucket) => bucket switch
    {
        "people" => People,
        "trips" => Trips,
        "requests" => Requests,
        _ => null,
    };
}

public static class ChatLocks
{
    public const string None = "none";
    public const string AwaitingReply = "awaitingReply";
    public const string Limit = "limit";
    public const string Left = "left";
}

public static class ChatSystemKinds
{
    public const string Joined = "joined";
    public const string Left = "left";

    public static readonly IReadOnlyList<string> All = [Joined, Left];
    public static readonly string SqlList = SocialSqlList.List(All);
}

// ListingReports.ListingType values of the social feature (User already exists in ReportScopes).
public static class SocialReportTypes
{
    public const string TripPost = "TripPost";
    public const string Message = "Message";

    public static readonly IReadOnlyList<string> All = [TripPost, Message];
}

// Scopes of the NotificationHub event SocialChanged.
public static class SocialLiveScopes
{
    public const string Followers = "followers";
    public const string FollowRequests = "followRequests";
    public const string JoinRequests = "joinRequests";
    public const string MyJoinRequests = "myJoinRequests";
    public const string Posts = "posts";
    public const string Limits = "limits";
    public const string Profile = "profile";
}

// SignalR events of the social chat (always to dm_{userId} groups).
public static class SocialHubEventNames
{
    public const string DmMessage = "DmMessage";
    public const string DmRead = "DmRead";
    public const string DmRoomChanged = "DmRoomChanged";
    public const string DmUnread = "DmUnread";
    public const string SessionEnded = "SessionEnded";

    public static string Group(Guid userId) => $"dm_{userId}";
}
