using RentNearBy.Core.Models.Buddy;

namespace RentNearBy.Core.Models.Social;

// Contract 2.9: type -> HTTP status and default message (same table style as BuddyErrorTypes). Reused buddy types
// (PersonNotFound, TripNotFound, TripLimitReached, BuddyLimit, TooManyRequests) fall back to BuddyErrorTypes.
public static class SocialErrorTypes
{
    public const string ProfileRequired = "ProfileRequired";
    public const string SocialOnHold = "SocialOnHold";
    public const string Under18 = "Under18";
    public const string IdentityLocked = "IdentityLocked";
    public const string InvalidProfile = "InvalidProfile";
    public const string InvalidPost = "InvalidPost";
    public const string InvalidCities = "InvalidCities";
    public const string TextNotAllowed = "TextNotAllowed";
    public const string TextTooLong = "TextTooLong";
    public const string FollowingLimit = "FollowingLimit";
    public const string RequestGone = "RequestGone";
    public const string NotFollower = "NotFollower";
    public const string TripPostNotFound = "TripPostNotFound";
    // Spec 22: a join request to a Women only / Men only trip from the other gender (403, data {joinGender}).
    public const string TripGenderMismatch = "TripGenderMismatch";
    public const string TripNotPostable = "TripNotPostable";
    public const string PlusRequired = "PlusRequired";
    public const string PublicTripLimit = "PublicTripLimit";
    public const string JoinClosed = "JoinClosed";
    public const string TripFull = BuddyErrorTypes.TripFull;
    public const string AlreadyMember = "AlreadyMember";
    public const string AlreadyRequested = "AlreadyRequested";
    public const string AskAgainLater = "AskAgainLater";
    public const string JoinRequestLimit = "JoinRequestLimit";
    public const string JoinRequestGone = "JoinRequestGone";
    public const string CouldNotAdd = "CouldNotAdd";
    public const string OpensAtStart = BuddyErrorTypes.OpensAtStart;
    public const string RoomNotFound = "RoomNotFound";
    public const string CannotMessage = "CannotMessage";
    public const string AwaitingReply = "AwaitingReply";
    public const string NewChatLimit = "NewChatLimit";
    public const string MessageGone = "MessageGone";
    public const string NotAllowed = "NotAllowed";
    // PLAN_STATES_SOCIAL 3: a write to a trip kept inactive after Plus ended (409, data {limit}).
    public const string TripInactiveByPlan = "TripInactiveByPlan";
    public const string SeekerOnly = "SeekerOnly";
    // Spec 23.9: a second name change inside 14 days (409, data {nextAllowedAt}).
    public const string NameChangeTooSoon = "NameChangeTooSoon";

    public const string PersonNotFound = BuddyErrorTypes.PersonNotFound;
    public const string TripNotFound = BuddyErrorTypes.TripNotFound;
    public const string TripLimitReached = BuddyErrorTypes.TripLimitReached;
    public const string BuddyLimit = BuddyErrorTypes.BuddyLimit;
    public const string TooManyRequests = BuddyErrorTypes.TooManyRequests;

    private static readonly Dictionary<string, (int Status, string Message)> Table = new()
    {
        [ProfileRequired] = (403, "Complete your profile first."),
        [SocialOnHold] = (403, "You cannot do this right now."),
        [Under18] = (400, "Bakhli is for people who are 18 or over."),
        [IdentityLocked] = (409, "Gender and birth date cannot be changed. Contact support if they are wrong."),
        [InvalidProfile] = (400, "Check your profile details."),
        [InvalidPost] = (400, "Check your trip details."),
        [InvalidCities] = (400, "Pick cities from one state."),
        [TextNotAllowed] = (400, "Remove phone numbers, emails and links."),
        [TextTooLong] = (400, "Keep it under 500 characters."),
        [FollowingLimit] = (409, "You follow 2,000 people. Unfollow someone to follow more."),
        [RequestGone] = (404, "This request is no longer open."),
        [NotFollower] = (409, "You can add only people who follow you."),
        [TripPostNotFound] = (404, "This trip is not available."),
        [TripGenderMismatch] = (403, "This trip is for one gender only."),
        [TripNotPostable] = (409, "This trip is complete."),
        [PlusRequired] = (403, "This is a Bakhli Plus feature."),
        [PublicTripLimit] = (429, "You have reached your public trips limit."),
        [JoinClosed] = (409, "This trip is not taking requests."),
        [AlreadyMember] = (409, "You are already on this trip."),
        [AlreadyRequested] = (409, "You already asked to join."),
        [AskAgainLater] = (409, "You can ask again later."),
        [JoinRequestLimit] = (429, "You have used your join requests for this month."),
        [JoinRequestGone] = (409, "This request is no longer waiting."),
        [CouldNotAdd] = (409, "This person cannot join right now."),
        [RoomNotFound] = (404, "Chat not found."),
        [CannotMessage] = (403, "You cannot message this person."),
        [AwaitingReply] = (409, "Wait for a reply before you send more."),
        [NewChatLimit] = (429, "You have used your new chats for this month."),
        [MessageGone] = (410, "This message is gone."),
        [NotAllowed] = (409, "This cannot be done here."),
        [TripInactiveByPlan] = (409, "This trip is inactive on the Free plan. Buy Plus to use it again."),
        [SeekerOnly] = (403, AuthPolicies.SeekerOnlyMessage),
        [NameChangeTooSoon] = (409, "You can change your name once every 14 days."),
    };

    public static IReadOnlyCollection<string> All => Table.Keys;

    public static int StatusOf(string type) => Table.TryGetValue(type, out var e) ? e.Status : BuddyErrorTypes.StatusOf(type);

    public static string MessageOf(string type) => Table.TryGetValue(type, out var e) ? e.Message : BuddyErrorTypes.MessageOf(type);

    // A monthly plan cap (429 with {limit, used, resetsAt, canUpgrade}).
    public static bool IsCap(string type) => type is PublicTripLimit or JoinRequestLimit or NewChatLimit;
}
