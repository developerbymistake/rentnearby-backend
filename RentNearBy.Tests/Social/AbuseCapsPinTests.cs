using RentNearBy.Core.Models.Social;
using RentNearBy.Tests.Buddy.Foundation;

namespace RentNearBy.Tests.Social;

// Abuse guards (Buddy_resume/post_notify/SPEC.md use cases 25-32): every cap is one named constant and the loops that could
// refund a cap use another key or a stored history.
public class AbuseCapsPinTests
{
    private static string Src(params string[] parts) => File.ReadAllText(Path.Combine([BuddySources.Root(), .. parts]));

    [Fact]
    public void All_caps_are_named_in_SocialLimits()
    {
        Assert.Equal(
            (2, 1, 4, 50, 72, 50),
            (SocialLimits.NotifiedPostsPerWeek, SocialLimits.NotifiedPostsFirstDay, SocialLimits.PublishesPerWeek, SocialLimits.PendingRequestsMax,
                SocialLimits.PostLockHours, SocialLimits.FollowsPerDay));
    }

    [Fact]
    public void Follow_cap_is_50_per_rolling_24_hours_and_unfollow_never_refunds_it()
    {
        var me = Guid.NewGuid();
        var follow = SocialRateLimits.Follow(me);
        Assert.Equal(SocialLimits.FollowsPerDay, follow.Max);
        Assert.Equal(TimeSpan.FromHours(24), follow.Window);
        Assert.NotEqual(follow.Key, SocialRateLimits.Write(me).Key);
        var handlers = Src("RentNearBy.Api", "Handlers", "Social", "FollowHandlers.cs");
        var followAt = handlers.IndexOf("public static Task<IResult> Follow(", StringComparison.Ordinal);
        var unfollowAt = handlers.IndexOf("public static Task<IResult> Unfollow(", StringComparison.Ordinal);
        Assert.Contains("SocialRateLimits.Follow(", handlers[followAt..unfollowAt]);
        var unfollow = handlers[unfollowAt..];
        var unfollowEnd = Math.Min(unfollow.Length, unfollow.IndexOf("=> SocialHttp.WriteAsync", StringComparison.Ordinal) + 200);
        Assert.Contains("SocialRateLimits.Write(", unfollow[..unfollowEnd]);
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
    public void Publish_and_notify_history_lives_on_the_profile_so_a_deleted_post_never_refunds()
    {
        var notifier = Src("RentNearBy.Infrastructure", "Services", "Social", "PostNotifier.cs");
        Assert.Contains("PublishTimes", notifier);
        Assert.Contains("NotifiedPostTimes", notifier);
        Assert.DoesNotContain("FROM \"Trips\" t WHERE t.\"OwnerUserId\" = @me AND t.\"PublishedAt\" >", notifier);
    }
}
