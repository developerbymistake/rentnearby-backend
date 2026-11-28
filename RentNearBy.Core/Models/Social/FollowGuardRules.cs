namespace RentNearBy.Core.Models.Social;

// Discover follow guard numbers (DESIGN 4.2); the Lua scripts and FollowGuardModel are built from them. Same for every plan.
public static class FollowGuardRules
{
    // Attempts per 60 s, refused ones too; the 11th attempt blocks.
    public const int BurstMax = 10;
    public const long BurstWindowMs = 60_000;

    // Counted follow requests per rolling 24 h (0 = rule off, in Redis and in the DB) and per rolling 7 days.
    public const int DayMax = 3;
    public const int WeekMax = 5;
    public const long DayMs = 24L * 60 * 60 * 1000;
    public const long WeekMs = 7 * DayMs;

    // Wasted attempts that reached the DB per rolling 24 h; the 5th blocks.
    public const int HammerMax = 5;
    public const long HammerWindowMs = DayMs;

    // A re-follow of a person unfollowed (or whose request was cancelled) within ChurnWindow is a churn event; the 3rd in 7 days blocks.
    public const int ChurnMax = 3;
    public const long ChurnWindowMs = 7 * DayMs;
    public const int UnfollowedKeep = 500;

    // Block ladder: level 1 = 24 h, 2 = 72 h, 3 = 7 days (level 3 repeats); back to level 0 after 30 days with no new block.
    public const int LevelMax = 3;
    public const long Level1Ms = DayMs;
    public const long Level2Ms = 3 * DayMs;
    public const long Level3Ms = 7 * DayMs;
    public const long LevelForgetMs = 30 * DayMs;

    // The "hydrated" marker lives 8 days and is refreshed on every TRY.
    public const long HydratedTtlMs = 8 * DayMs;

    public static readonly TimeSpan Day = TimeSpan.FromMilliseconds(DayMs);
    public static readonly TimeSpan Week = TimeSpan.FromMilliseconds(WeekMs);
    public static readonly TimeSpan LevelForget = TimeSpan.FromMilliseconds(LevelForgetMs);

    public static readonly IReadOnlyList<long> LadderMs = [Level1Ms, Level2Ms, Level3Ms];

    // Block length of a ladder level (1..3; above 3 = 3).
    public static TimeSpan BlockLength(int level) => TimeSpan.FromMilliseconds(LadderMs[Math.Clamp(level, 1, LevelMax) - 1]);

    // The level the next block gets: the stored level is forgotten after LevelForget with no block.
    public static int NextLevel(int storedLevel, DateTime? levelAt, DateTime nowUtc)
    {
        var level = levelAt is { } at && nowUtc - at <= LevelForget ? Math.Max(storedLevel, 0) : 0;
        return Math.Min(level + 1, LevelMax);
    }

    // Counted send times still inside the 7-day window, oldest first.
    public static DateTime[] InWeek(IEnumerable<DateTime>? times, DateTime nowUtc)
        => (times ?? []).Where(t => t > nowUtc - Week).OrderBy(t => t).ToArray();

    public static int CountIn(IEnumerable<DateTime>? times, DateTime nowUtc, TimeSpan window)
        => (times ?? []).Count(t => t > nowUtc - window);

    // The exact DB check (4.5): week full, or the day rule is on and the day is full.
    public static bool IsFull(IEnumerable<DateTime>? times, DateTime nowUtc)
    {
        var list = (times ?? []).ToArray();
        return CountIn(list, nowUtc, Week) >= WeekMax || (DayMax > 0 && CountIn(list, nowUtc, Day) >= DayMax);
    }

    // The stored FollowSendTimes after one more counted follow: in-window times + now.
    public static DateTime[] Append(IEnumerable<DateTime>? times, DateTime nowUtc) => [.. InWeek(times, nowUtc), nowUtc];
}

// Answers of the TRY / WASTED Lua scripts: {code, untilMs, level}.
public static class FollowGuardCodes
{
    public const int Reserved = 0;
    public const int Full = 1;
    public const int Blocked = 2;
    public const int BlockedNew = 3;
    public const int Hydrate = 9;
}

// Redis keys of one user. The {guid} hash tag keeps them in one cluster slot, so one Lua call over KEYS is atomic and cluster-safe.
public static class FollowGuardKeys
{
    public const string Prefix = "fg:";

    public static string Tag(Guid userId) => $"{{{userId:N}}}";
    public static string Hydrated(Guid userId) => $"{Prefix}{Tag(userId)}:h";
    public static string Sends(Guid userId) => $"{Prefix}{Tag(userId)}:s";
    public static string Attempts(Guid userId) => $"{Prefix}{Tag(userId)}:a";
    public static string Wasted(Guid userId) => $"{Prefix}{Tag(userId)}:x";
    public static string Churn(Guid userId) => $"{Prefix}{Tag(userId)}:c";
    public static string Block(Guid userId) => $"{Prefix}{Tag(userId)}:b";
    public static string Unfollowed(Guid userId) => $"{Prefix}{Tag(userId)}:u";

    // The one KEYS order of every script: h, s, a, x, c, b, u (KEYS[1]..KEYS[7]).
    public static string[] All(Guid userId) =>
        [Hydrated(userId), Sends(userId), Attempts(userId), Wasted(userId), Churn(userId), Block(userId), Unfollowed(userId)];
}
