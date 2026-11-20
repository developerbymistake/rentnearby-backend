namespace RentNearBy.Core.Models.Buddy;

public sealed record BuddyRateLimit(string Key, int Max, TimeSpan Window);

// Contract 2.0: sliding window keys and limits.
public static class BuddyRateLimitKeys
{
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);
    private static readonly TimeSpan Day = TimeSpan.FromHours(24);

    public static BuddyRateLimit Find(Guid userId) => new($"buddy:find:{userId}", BuddyLimits.FindPerHour, Hour);
    public static BuddyRateLimit FindIp(string ip) => new($"buddy:find:ip:{ip}", BuddyLimits.FindPerHourPerIp, Hour);
    public static BuddyRateLimit Request(Guid userId) => new($"buddy:request:{userId}", BuddyLimits.RequestsPerDay, Day);
    public static BuddyRateLimit Block(Guid userId) => new($"buddy:block:{userId}", BuddyLimits.BlocksPerHour, Hour);
    public static BuddyRateLimit Report(Guid userId) => new($"report:submit:{userId}", BuddyLimits.ReportsPerHour, Hour);
    public static BuddyRateLimit BuddyRead(Guid userId) => new($"buddy:read:{userId}", 120, Minute);
    public static BuddyRateLimit BuddyWrite(Guid userId) => new($"buddy:write:{userId}", 60, Minute);
    public static BuddyRateLimit TripInvite(Guid userId) => new($"trip:invite:{userId}", BuddyLimits.InvitesPerDay, Day);
    public static BuddyRateLimit TripJoin(Guid userId) => new($"trip:join:{userId}", 20, Hour);
    public static BuddyRateLimit TripMembers(Guid userId) => new($"trip:members:{userId}", 60, Hour);
    public static BuddyRateLimit TripView(Guid userId) => new($"trip:view:{userId}", 120, Minute);
    public static BuddyRateLimit ExpenseRead(Guid userId) => new($"exp:read:{userId}", 60, Minute);
    public static BuddyRateLimit ExpenseWrite(Guid userId) => new($"exp:write:{userId}", 30, Minute);
    public static BuddyRateLimit ExpensePay(Guid userId) => new($"exp:pay:{userId}", 10, Minute);
    public static BuddyRateLimit NoticeRead(Guid userId) => new($"notice:read:{userId}", 120, Minute);
}
