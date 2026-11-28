using RentNearBy.Core.Models.Social;
using RentNearBy.Tests.Buddy.Foundation;

namespace RentNearBy.Tests.Social;

// Abuse guards (Buddy_resume/post_notify/SPEC.md use cases 25-32): every cap is one named constant and the loops that could
// refund a cap use another key or a stored history.
public class AbuseCapsPinTests
{
    private static string Src(params string[] parts) => File.ReadAllText(Path.Combine([BuddySources.Root(), .. parts]));

    [Fact]
    public void All_caps_are_named_in_SocialLimits_and_FollowGuardRules()
    {
        Assert.Equal((4, 50), (SocialLimits.PublishesPerWeek, SocialLimits.PendingRequestsMax));
        Assert.Equal((10, 3, 5), (FollowGuardRules.BurstMax, FollowGuardRules.DayMax, FollowGuardRules.WeekMax));
    }

    // Discover DESIGN 1.5 / 4.1: the old 50-per-24h follow cap is gone; the follow route runs the Redis follow guard
    // (week 5, day 3, burst, blocks) and answers TryLater; unfollow keeps the Write key and never refunds a follow.
    [Fact]
    public void Follow_uses_the_follow_guard_and_unfollow_never_refunds_it()
    {
        Assert.Null(typeof(SocialRateLimits).GetMethod("Follow"));
        Assert.Null(typeof(SocialLimits).GetField("FollowsPerDay"));
        var handlers = Src("RentNearBy.Api", "Handlers", "Social", "FollowHandlers.cs");
        var followAt = handlers.IndexOf("public static async Task<IResult> Follow(", StringComparison.Ordinal);
        var unfollowAt = handlers.IndexOf("public static Task<IResult> Unfollow(", StringComparison.Ordinal);
        Assert.True(followAt >= 0 && unfollowAt > followAt);
        var follow = handlers[followAt..unfollowAt];
        Assert.Contains("guard.TryAsync(", follow);
        Assert.Contains("SocialErrorTypes.TryLater", follow);
        Assert.DoesNotContain("SocialRateLimits.", follow);
        Assert.DoesNotContain("ActionLimitAsync", follow);
        var unfollow = handlers[unfollowAt..];
        var unfollowEnd = Math.Min(unfollow.Length, unfollow.IndexOf("=> SocialHttp.WriteAsync", StringComparison.Ordinal) + 200);
        Assert.Contains("SocialRateLimits.Write(", unfollow[..unfollowEnd]);
        Assert.DoesNotContain("Release", Src("RentNearBy.Infrastructure", "Services", "Social", "FollowService.cs"));
    }

    [Theory]
    [InlineData(SocialErrorTypes.PlusRequired, false)]         // safety review 2: a scripted Free sender stays throttled at 30 / min
    [InlineData(SocialErrorTypes.GuidelinesNotAccepted, true)]
    [InlineData(SocialErrorTypes.AwaitingReply, true)]
    [InlineData(SocialErrorTypes.RoomNotFound, false)]          // probing unknown rooms keeps costing
    [InlineData(SocialErrorTypes.CannotMessage, false)]
    public void A_chat_send_that_stored_nothing_gives_its_permit_back(string type, bool givesBack)
        => Assert.Equal(givesBack, SocialRateLimits.ChatSendGivesBack(type));

    [Fact]
    public void Rest_and_hub_sends_share_the_permit_rule()
    {
        Assert.Contains("SocialRateLimits.ChatSendGivesBack(result.ErrorType!)", Src("RentNearBy.Api", "Handlers", "Social", "SocialChatHandlers.cs"));
        Assert.Contains("SocialRateLimits.ChatSendGivesBack(result.ErrorType!)", Src("RentNearBy.Api", "Hubs", "ChatHub.cs"));
    }

    [Fact]
    public void Publish_history_lives_on_the_profile_so_a_deleted_post_never_refunds()
    {
        var notifier = Src("RentNearBy.Infrastructure", "Services", "Social", "PostNotifier.cs");
        Assert.Contains("PublishTimes", notifier);
        // I-2 (owner 2026-11-28): no weekly notified-post cap any more.
        Assert.DoesNotContain("NotifiedPostTimes", notifier);
        Assert.DoesNotContain("FROM \"Trips\" t WHERE t.\"OwnerUserId\" = @me AND t.\"PublishedAt\" >", notifier);
    }
}
