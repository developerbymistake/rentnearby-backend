using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Services.Social;

namespace RentNearBy.Tests.Social;

// Safety review 2 (Buddy_resume/post_notify/review2_safety.md, 2026-11-27): edges the first safety review did not pin.
public class PostNotifyReview2Tests
{
    private static readonly DateTime Now = new(2026, 11, 27, 6, 0, 0, DateTimeKind.Utc);

    // A server clock that jumped ahead and came back left "future" uses in the history: they keep counting (never a refund)
    // until they really leave the window.
    [Fact]
    public void A_clock_rollback_never_refunds_the_publish_cap()
    {
        var poster = new PostNotifier.Poster
        {
            AccountCreatedAt = Now.AddDays(-100),
            PublishTimes = [Now.AddDays(-1), Now.AddHours(-2), Now.AddHours(3), Now.AddHours(5)],
        };
        Assert.NotNull(PostNotifier.PublishRefusal(poster, Now));
        Assert.Equal(4, PostNotifyRules.InWindow(poster.PublishTimes, Now).Length);
        Assert.Equal(4, PostNotifyRules.Append(poster.PublishTimes, Now).Length - 1);
    }

    // Exactly 7 days old leaves the window, and the next allowed moment is exactly when that happens (no off-by-one gap).
    [Fact]
    public void The_publish_cap_frees_at_exactly_seven_days()
    {
        var oldest = Now - PostNotifyRules.Window;
        var poster = new PostNotifier.Poster { PublishTimes = [oldest, Now.AddDays(-3), Now.AddDays(-2), Now.AddDays(-1)] };
        Assert.Null(PostNotifier.PublishRefusal(poster, Now));
        Assert.NotNull(PostNotifier.PublishRefusal(poster, Now.AddTicks(-1)));
        var inWindow = PostNotifyRules.InWindow([oldest.AddTicks(1), Now.AddDays(-3), Now.AddDays(-2), Now.AddDays(-1)], Now);
        Assert.Equal(oldest.AddTicks(1) + PostNotifyRules.Window, PostNotifyRules.NextAllowedAt(inWindow, PostNotifyRules.PublishesPerWeek));
    }

    // A Free member's send into a trip room keeps its 30 / min ChatSend permit: only a stale or scripted client sends there
    // (the app shows the Plus panel in place of the composer), and there is no global limiter behind it.
    [Fact]
    public void A_refused_group_chat_send_keeps_the_anti_flood_permit()
    {
        Assert.False(SocialRateLimits.ChatSendGivesBack(SocialErrorTypes.PlusRequired));
        Assert.Equal(403, SocialErrorTypes.StatusOf(SocialErrorTypes.PlusRequired));
    }
}
