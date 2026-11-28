using RentNearBy.Core.Models.Social;

namespace RentNearBy.Tests.Social.Discover;

// Discover follow guard numbers and the exact DB check (DESIGN 4.2, 4.5); the Lua parity model is FollowGuardModel (lane L1).
public class FollowGuardRulesTests
{
    private static readonly DateTime Now = new(2026, 11, 28, 6, 0, 0, DateTimeKind.Utc);

    private static DateTime Ago(double days) => Now.AddDays(-days);

    [Fact]
    public void Numbers_match_the_owner_decisions()
    {
        Assert.Equal(10, FollowGuardRules.BurstMax);
        Assert.Equal(60_000L, FollowGuardRules.BurstWindowMs);
        Assert.Equal(3, FollowGuardRules.DayMax);
        Assert.Equal(5, FollowGuardRules.WeekMax);
        Assert.Equal(86_400_000L, FollowGuardRules.DayMs);
        Assert.Equal(604_800_000L, FollowGuardRules.WeekMs);
        Assert.Equal(TimeSpan.FromHours(24), FollowGuardRules.BlockLength(1));
        Assert.Equal(TimeSpan.FromHours(72), FollowGuardRules.BlockLength(2));
        Assert.Equal(TimeSpan.FromDays(7), FollowGuardRules.BlockLength(3));
        Assert.Equal(TimeSpan.FromDays(7), FollowGuardRules.BlockLength(4));
        Assert.Equal(TimeSpan.FromHours(24), FollowGuardRules.BlockLength(0));
    }

    [Fact]
    public void Ladder_climbs_to_seven_days_and_forgets_after_thirty_days()
    {
        Assert.Equal(1, FollowGuardRules.NextLevel(0, null, Now));
        Assert.Equal(2, FollowGuardRules.NextLevel(1, Ago(1), Now));
        Assert.Equal(3, FollowGuardRules.NextLevel(2, Ago(1), Now));
        Assert.Equal(3, FollowGuardRules.NextLevel(3, Ago(1), Now));
        Assert.Equal(3, FollowGuardRules.NextLevel(2, Ago(30), Now));
        Assert.Equal(1, FollowGuardRules.NextLevel(2, Ago(31), Now));
        Assert.Equal(1, FollowGuardRules.NextLevel(2, null, Now));
    }

    [Fact]
    public void Week_is_full_at_five_and_a_slot_frees_exactly_seven_days_after_the_oldest()
    {
        DateTime[] five = [Ago(6), Ago(5), Ago(4), Ago(3), Ago(2)];
        Assert.True(FollowGuardRules.IsFull(five, Now));
        DateTime[] oldestAtSeven = [Ago(7), Ago(5), Ago(4), Ago(3), Ago(2)];
        Assert.False(FollowGuardRules.IsFull(oldestAtSeven, Now));
        Assert.False(FollowGuardRules.IsFull([Ago(6), Ago(5), Ago(4), Ago(3)], Now));
    }

    [Fact]
    public void Day_is_full_at_three_even_with_the_week_free()
    {
        DateTime[] three = [Now.AddHours(-20), Now.AddHours(-10), Now.AddMinutes(-1)];
        Assert.True(FollowGuardRules.IsFull(three, Now));
        DateTime[] twoToday = [Now.AddHours(-10), Now.AddMinutes(-1), Ago(2)];
        Assert.False(FollowGuardRules.IsFull(twoToday, Now));
        Assert.Equal(2, FollowGuardRules.CountIn(twoToday, Now, FollowGuardRules.Day));
        Assert.False(FollowGuardRules.IsFull([], Now));
        Assert.False(FollowGuardRules.IsFull(null, Now));
    }

    [Fact]
    public void Append_keeps_only_the_window_and_adds_now()
    {
        var times = FollowGuardRules.Append([Ago(9), Ago(3), Ago(1)], Now);
        Assert.Equal([Ago(3), Ago(1), Now], times);
        Assert.Equal([Ago(3), Ago(1)], FollowGuardRules.InWeek([Ago(1), Ago(8), Ago(3)], Now));
    }

    [Fact]
    public void Keys_share_one_hash_tag_in_the_one_script_order()
    {
        var u = Guid.Parse("0123456789abcdef0123456789abcdef");
        Assert.Equal("fg:{0123456789abcdef0123456789abcdef}:h", FollowGuardKeys.Hydrated(u));
        Assert.Equal(
            ["fg:{0123456789abcdef0123456789abcdef}:h", "fg:{0123456789abcdef0123456789abcdef}:s", "fg:{0123456789abcdef0123456789abcdef}:a",
             "fg:{0123456789abcdef0123456789abcdef}:x", "fg:{0123456789abcdef0123456789abcdef}:c", "fg:{0123456789abcdef0123456789abcdef}:b",
             "fg:{0123456789abcdef0123456789abcdef}:u"],
            FollowGuardKeys.All(u));
    }

    [Fact]
    public void Script_codes_are_fixed()
    {
        Assert.Equal(0, FollowGuardCodes.Reserved);
        Assert.Equal(1, FollowGuardCodes.Full);
        Assert.Equal(2, FollowGuardCodes.Blocked);
        Assert.Equal(3, FollowGuardCodes.BlockedNew);
        Assert.Equal(9, FollowGuardCodes.Hydrate);
    }

    [Fact]
    public void TryLater_is_one_generic_429_without_numbers()
    {
        Assert.Equal(429, SocialErrorTypes.StatusOf(SocialErrorTypes.TryLater));
        Assert.Equal("Try again later. We limit some actions to keep Bakhli safe.", SocialErrorTypes.MessageOf(SocialErrorTypes.TryLater));
        Assert.Contains(SocialErrorTypes.TryLater, SocialErrorTypes.All);
        Assert.False(SocialErrorTypes.IsCap(SocialErrorTypes.TryLater));
        Assert.False(SocialErrorTypes.MessageOf(SocialErrorTypes.TryLater).Any(char.IsAsciiDigit));
    }
}
