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
    public static BuddyRateLimit Follow(Guid userId) => new($"social:follow:{userId}", 50, Day);
    public static BuddyRateLimit ProfileEdit(Guid userId) => new($"social:profile:{userId}", 10, Day);
    public static BuddyRateLimit Publish(Guid userId) => new($"social:publish:{userId}", 10, Hour);
    public static BuddyRateLimit JoinSend(Guid userId) => new($"social:join:{userId}", 20, Hour);
    public static BuddyRateLimit ChatNew(Guid userId) => new($"social:chatnew:{userId}", NewChatsPerDay, Day);
    public static BuddyRateLimit ChatSend(Guid userId) => new($"social:chatsend:{userId}", 30, Minute);
    public static BuddyRateLimit Report(Guid userId) => BuddyRateLimitKeys.Report(userId);
}
