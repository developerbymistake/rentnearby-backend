using RentNearBy.Core.Models.Buddy;

namespace RentNearBy.Core.Models.Social;

// Contract 2: sliding-window keys of the social routes (used through BuddyHttp.ReadLimitAsync / ActionLimitAsync).
public static class SocialRateLimits
{
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);
    private static readonly TimeSpan Day = TimeSpan.FromHours(24);

    // Owner correction (contract 6, Q3): at most 5 new chats a day on every plan, on top of the monthly plan cap.
    public const int NewChatsPerDay = 5;

    public static BuddyRateLimit Read(Guid userId) => new($"social:read:{userId}", 120, Minute);
    public static BuddyRateLimit Write(Guid userId) => new($"social:write:{userId}", 60, Minute);
    public static BuddyRateLimit ProfileEdit(Guid userId) => new($"social:profile:{userId}", 10, Day);
    public static BuddyRateLimit Publish(Guid userId) => new($"social:publish:{userId}", 10, Hour);
    public static BuddyRateLimit JoinSend(Guid userId) => new($"social:join:{userId}", 20, Hour);
    public static BuddyRateLimit ChatNew(Guid userId) => new($"social:chatnew:{userId}", NewChatsPerDay, Day);
    public static BuddyRateLimit ChatSend(Guid userId) => new($"social:chatsend:{userId}", 30, Minute);
    // PUT /social/me/area: app start / resume / district change; the write itself is skipped while nothing changed (6 h).
    public static BuddyRateLimit Area(Guid userId) => new($"social:area:{userId}", 30, Day);
    public static BuddyRateLimit Report(Guid userId) => BuddyRateLimitKeys.Report(userId);

    // A chat send that stored nothing gives its ChatSend permit back (REST and hub): every refusal except 401/403/404, plus the
    // guidelines refusal. PlusRequired (403) keeps costing (safety review 2): the honest app shows no composer to a Free member,
    // so only a stale or scripted client sends there, and with the permit given back nothing would throttle that loop (there
    // is no global limiter; each refused send still costs the plan read and the room read).
    public static bool ChatSendGivesBack(string errorType)
        => errorType is SocialErrorTypes.GuidelinesNotAccepted
           || SocialErrorTypes.StatusOf(errorType) is not (401 or 403 or 404);
}
