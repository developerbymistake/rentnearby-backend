using RentNearBy.Core.Models.Social;

namespace RentNearBy.Tests.Social;

// Pure rules: 18+, gender visibility, month-only projection, seats, join tier, names.
public class SocialRulesTests
{
    [Theory]
    [InlineData(2008, 11, true)]
    [InlineData(2008, 12, false)]
    [InlineData(1990, 1, true)]
    [InlineData(2010, 5, false)]
    public void Adult_from_216_months(int year, int month, bool adult)
        => Assert.Equal(adult, SocialRules.IsAdult(year, month, new DateOnly(2026, 11, 26)));

    // Spec 23.9: name change once every 14 days; null = may change now.
    [Fact]
    public void Name_change_allowed_at_is_14_days_after_the_last_change()
    {
        var now = new DateTime(2026, 11, 27, 6, 0, 0, DateTimeKind.Utc);
        Assert.Null(SocialRules.NameChangeAllowedAt(null, now));
        Assert.Equal(now.AddDays(13), SocialRules.NameChangeAllowedAt(now.AddDays(-1), now));
        Assert.Equal(now.AddSeconds(1), SocialRules.NameChangeAllowedAt(now.AddDays(-14).AddSeconds(1), now));
        Assert.Null(SocialRules.NameChangeAllowedAt(now.AddDays(-14), now));
        Assert.Null(SocialRules.NameChangeAllowedAt(now.AddDays(-30), now));
        Assert.Equal(14, SocialLimits.NameChangeDays);
    }

    [Theory]
    [InlineData("Asha", "Asha", false)]
    [InlineData(" Asha ", "Asha", false)]
    [InlineData("Asha", "asha", true)]
    [InlineData("Asha", "Asha Rawat", true)]
    [InlineData(null, "Asha", true)]
    public void Only_a_different_name_is_a_change(string? current, string next, bool change)
        => Assert.Equal(change, SocialRules.IsNameChange(current, next));

    [Fact]
    public void Name_change_too_soon_is_a_409()
    {
        Assert.Equal(409, SocialErrorTypes.StatusOf(SocialErrorTypes.NameChangeTooSoon));
        Assert.Equal("You can change your name once every 14 days.", SocialErrorTypes.MessageOf(SocialErrorTypes.NameChangeTooSoon));
    }

    [Fact]
    public void Live_post_twin_of_the_sql_rule()
    {
        var now = new DateTime(2026, 11, 26, 6, 0, 0, DateTimeKind.Utc);
        var published = now.AddDays(-10);
        Assert.True(SocialRules.IsLivePost(true, TripStatuses.Draft, null, 2, published, now));
        Assert.True(SocialRules.IsLivePost(true, TripStatuses.Planned, new DateOnly(2026, 11, 26), null, published, now));
        Assert.False(SocialRules.IsLivePost(false, TripStatuses.Draft, null, null, published, now));
        Assert.False(SocialRules.IsLivePost(true, TripStatuses.Completed, null, null, published, now));
        Assert.False(SocialRules.IsLivePost(true, TripStatuses.Draft, new DateOnly(2026, 11, 25), null, published, now));
        Assert.False(SocialRules.IsLivePost(true, TripStatuses.Draft, null, 1, now.AddMonths(-2), now));
    }

    [Theory]
    [InlineData("Male", "M")]
    [InlineData("Female", "F")]
    [InlineData("Other", null)]
    [InlineData(null, null)]
    public void Gender_code(string? gender, string? code) => Assert.Equal(code, SocialRules.GenderCode(gender));

    // Spec 22: every post is visible to everyone; only the join request is gender-checked.
    [Theory]
    [InlineData("A", "Other", true)]
    [InlineData("A", "Male", true)]
    [InlineData("A", null, true)]
    [InlineData("F", "Female", true)]
    [InlineData("F", "Male", false)]
    [InlineData("F", "Other", false)]
    [InlineData("F", null, false)]
    [InlineData("M", "Male", true)]
    [InlineData("M", "Female", false)]
    [InlineData("M", "Other", false)]
    public void Women_only_takes_only_women_and_men_only_only_men(string joinGender, string? viewer, bool canJoin)
        => Assert.Equal(canJoin, SocialRules.CanJoin(joinGender, viewer));

    [Fact]
    public void Only_women_only_needs_plus()
    {
        Assert.False(SocialRules.NeedsPlus(JoinGenders.Anyone));
        Assert.False(SocialRules.NeedsPlus(JoinGenders.Men));
        Assert.True(SocialRules.NeedsPlus(JoinGenders.Women));
    }

    [Theory]
    [InlineData(null, true, null)]
    [InlineData("any", true, null)]
    [InlineData("anyone", true, "A")]
    [InlineData("women", true, "F")]
    [InlineData("men", true, "M")]
    [InlineData("mine", false, null)]
    [InlineData("Women", false, null)]
    public void Feed_who_filter_maps_to_a_join_rule(string? who, bool valid, string? code)
    {
        Assert.Equal(valid, JoinGenders.TryWhoFilter(who, out var got));
        Assert.Equal(code, got);
    }

    [Fact]
    public void Known_start_date_shows_only_its_month()
    {
        var w = SocialRules.When(new DateOnly(2027, 1, 14), null, 3, DateTime.UtcNow, DateTime.UtcNow);
        Assert.Equal(new WhenValue(SocialRules.WhenMonth, 202701), w);
    }

    [Fact]
    public void Exact_month_without_plan_date_shows_the_post_month()
        => Assert.Equal(new WhenValue(SocialRules.WhenMonth, 202612), SocialRules.When(null, 202612, null, DateTime.UtcNow, DateTime.UtcNow));

    [Theory]
    [InlineData(1, 2)]
    [InlineData(15, 2)]
    [InlineData(31, 1)]
    public void Roughly_window_shows_months_left_rounded_up(int nowDay, int expected)
    {
        var published = new DateTime(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc);
        var now = new DateTime(2026, nowDay == 31 ? 12 : 11, nowDay, 10, 0, 0, DateTimeKind.Utc);
        Assert.Equal(new WhenValue(SocialRules.WhenWithin, expected), SocialRules.When(null, null, 3, published, now));
    }

    [Fact]
    public void Window_never_reads_below_one_month()
        => Assert.Equal(1, SocialRules.When(null, null, 1, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)).Value);

    [Theory]
    [InlineData(3, 1, 2)]
    [InlineData(1, 2, 0)]
    [InlineData(11, 4, 7)]
    public void Seats_left_floor_at_zero(int seats, int joined, int left) => Assert.Equal(left, SocialRules.SeatsLeft(seats, joined));

    [Theory]
    [InlineData(1, true)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    [InlineData(11, true)]
    public void Seats_choices(int seats, bool valid) => Assert.Equal(valid, SocialRules.IsValidSeats(seats));

    [Fact]
    public void Join_tier_buddies_then_followers_then_others()
    {
        Assert.Equal(0, SocialRules.Tier(true, true));
        Assert.Equal(1, SocialRules.Tier(false, true));
        Assert.Equal(2, SocialRules.Tier(false, false));
    }

    [Fact]
    public void Names_on_bubbles_and_initials()
    {
        Assert.Equal("Aman K", SocialRules.BubbleName("Aman Kumar"));
        Assert.Equal("Aman", SocialRules.BubbleName(" Aman "));
        Assert.Equal("Aman", SocialRules.FirstName("Aman Kumar"));
        Assert.Equal("A", SocialRules.Initial("aman"));
        Assert.Equal("?", SocialRules.Initial(""));
    }

    [Fact]
    public void Preview_is_cut_to_80_and_named_in_trip_rooms()
    {
        Assert.Equal(SocialLimits.ChatPreviewMax, SocialRules.Preview(new string('a', 200)).Length);
        Assert.Equal("Aman: hi", SocialRules.Preview("hi", "Aman"));
    }

    [Fact]
    public void Catalog_lists_need_known_values_inside_the_count()
    {
        Assert.NotNull(SocialRules.CatalogList(["Treks", "Food", "Culture"], SocialCatalog.Interests, 3, 5));
        Assert.Null(SocialRules.CatalogList(["Treks", "Food"], SocialCatalog.Interests, 3, 5));
        Assert.Null(SocialRules.CatalogList(["Treks", "Food", "Gaming"], SocialCatalog.Interests, 3, 5));
        Assert.True(SocialRules.PlaceHasContact("Call 9876543210"));
        Assert.False(SocialRules.PlaceHasContact("Spiti valley"));
    }

    [Fact]
    public void Message_policy()
    {
        Assert.True(SocialChatServiceProbe.Allows(MessagePolicies.Everyone, false, false));
        Assert.False(SocialChatServiceProbe.Allows(MessagePolicies.Following, false, true));
        Assert.True(SocialChatServiceProbe.Allows(MessagePolicies.Following, true, false));
        Assert.False(SocialChatServiceProbe.Allows(MessagePolicies.Buddies, true, false));
        Assert.True(SocialChatServiceProbe.Allows(MessagePolicies.Buddies, false, true));
    }

    [Fact]
    public void Free_starter_waits_for_a_reply_and_a_plus_starter_does_not()
    {
        Assert.Equal(ChatLocks.AwaitingReply, SocialChatServiceProbe.Lock("D", "Request", true, 1, false, false));
        Assert.Equal(ChatLocks.None, SocialChatServiceProbe.Lock("D", "Request", true, 1, false, true));
        Assert.Equal(ChatLocks.None, SocialChatServiceProbe.Lock("D", "Request", false, 1, false, false));
        Assert.Equal(ChatLocks.AwaitingReply, SocialChatServiceProbe.Lock("D", "Declined", true, 1, false, true));
        Assert.Equal(ChatLocks.Left, SocialChatServiceProbe.Lock("T", "Active", false, 9, true, false));
        // The decliner of a direct request can never send there (contract 2.6).
        Assert.Equal(ChatLocks.Left, SocialChatServiceProbe.Lock("D", "Declined", false, 1, false, true));
    }

    private static class SocialChatServiceProbe
    {
        public static bool Allows(string policy, bool heFollowsMe, bool inHisBuddies)
            => RentNearBy.Infrastructure.Services.Social.SocialChatService.PolicyAllows(policy, heFollowsMe, inHisBuddies);

        public static string Lock(string kind, string status, bool iStarted, long lastSeq, bool left, bool isPlus)
            => RentNearBy.Infrastructure.Services.Social.SocialChatService.LockOf(kind, status, iStarted, lastSeq, left, isPlus);
    }
}
