using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Services.Social;
using RentNearBy.Tests.Buddy.Foundation;

namespace RentNearBy.Tests.Social.Discover;

// Discover DESIGN 5.3: grouped follow pushes ("Rahul and 4 others followed you"), requests separate, 2 min gather, 1 h gap.
public class FollowPushCopyTests
{
    private static readonly Guid To = new("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly Guid Rahul = new("99999999-8888-7777-6666-555555555555");
    private static readonly DateTime Since = new(2026, 11, 28, 6, 0, 0, DateTimeKind.Utc);

    private static string N(Guid id) => id.ToString("N");

    [Fact]
    public void Window_is_two_minutes_gather_and_one_hour_gap()
    {
        Assert.Equal(TimeSpan.FromMinutes(2), SocialPushCopy.FollowPushGather);
        Assert.Equal(TimeSpan.FromMinutes(60), SocialPushCopy.FollowPushGap);
    }

    [Theory]
    [InlineData(1, "New follower", "Rahul followed you")]
    [InlineData(2, "New followers", "Rahul and 1 other followed you")]
    [InlineData(5, "New followers", "Rahul and 4 others followed you")]
    [InlineData(50, "New followers", "Rahul and 49 others followed you")]
    public void Followers_text(int n, string title, string body)
    {
        var p = SocialPushCopy.FollowersGrouped(To, n, Rahul, "Rahul", Since);
        Assert.Equal(title, p.Title);
        Assert.Equal(body, p.Body);
        Assert.Equal(NotificationTypes.SocialNewFollower, p.PushKind);
        Assert.Equal(To, p.UserId);
    }

    [Theory]
    [InlineData(1, "Follow request", "Rahul wants to follow you")]
    [InlineData(2, "Follow requests", "Rahul and 1 other want to follow you")]
    [InlineData(50, "Follow requests", "Rahul and 49 others want to follow you")]
    public void Requests_text(int n, string title, string body)
    {
        var p = SocialPushCopy.RequestsGrouped(To, n, "Rahul", Since);
        Assert.Equal(title, p.Title);
        Assert.Equal(body, p.Body);
        Assert.Equal(NotificationTypes.SocialFollowRequest, p.PushKind);
        Assert.Equal(UserNotificationRoutes.People, p.Route);
        Assert.Contains("\"tab\":\"requests\"", p.ArgsJson);
    }

    [Fact]
    public void One_follower_opens_his_profile_and_several_open_followers()
    {
        var one = SocialPushCopy.FollowersGrouped(To, 1, Rahul, "Rahul", Since);
        Assert.Equal(UserNotificationRoutes.Traveller, one.Route);
        Assert.Contains(Rahul.ToString(), one.ArgsJson);

        var many = SocialPushCopy.FollowersGrouped(To, 3, Rahul, "Rahul", Since);
        Assert.Equal(UserNotificationRoutes.People, many.Route);
        Assert.Contains("\"tab\":\"followers\"", many.ArgsJson);
    }

    [Fact]
    public void Key_is_per_group_and_tags_keep_requests_separate()
    {
        var ms = new DateTimeOffset(Since).ToUnixTimeMilliseconds();
        var a = SocialPushCopy.FollowersGrouped(To, 2, Rahul, "Rahul", Since);
        var r = SocialPushCopy.RequestsGrouped(To, 2, "Rahul", Since);
        Assert.Equal($"fol:{N(To)}:A:{ms}", a.Key);
        Assert.Equal($"fol:{N(To)}:R:{ms}", r.Key);
        Assert.Equal($"fol:{N(To)}", a.Tag);
        Assert.Equal($"folreq:{N(To)}", r.Tag);
        Assert.NotEqual(a.Tag, r.Tag);
        Assert.Equal(a.Key, SocialPushCopy.FollowersGrouped(To, 7, Rahul, "Aman", Since).Key);
        Assert.NotEqual(a.Key, SocialPushCopy.FollowersGrouped(To, 2, Rahul, "Rahul", Since.AddHours(1)).Key);
        Assert.True(a.Key.Length <= RentNearBy.Core.Models.Buddy.BuddyPushCopy.MaxKeyLength);
        Assert.Null(a.Expires);
        Assert.Null(r.Expires);
    }

    [Fact]
    public void Lock_screen_shows_only_a_name()
    {
        Assert.Equal("Someone followed you", SocialPushCopy.FollowersGrouped(To, 1, Rahul, "  ", Since).Body);
        Assert.Equal("Someone and 2 others want to follow you", SocialPushCopy.RequestsGrouped(To, 3, null, Since).Body);
        foreach (var p in new[] { SocialPushCopy.FollowersGrouped(To, 1, Rahul, " Rahul ", Since), SocialPushCopy.RequestsGrouped(To, 1, "Rahul", Since) })
        {
            Assert.StartsWith("Rahul ", p.Body);
            Assert.DoesNotContain("2026", p.Body);
            Assert.DoesNotContain("Nov", p.Body);
        }
    }

    [Fact]
    public void Open_groups_take_one_row_per_receiver_and_kind_with_the_oldest_follow()
    {
        var other = Guid.NewGuid();
        var batch = new[]
        {
            Row(Guid.NewGuid(), To, FollowStatuses.Accepted, Since.AddSeconds(5)),
            Row(Guid.NewGuid(), To, FollowStatuses.Accepted, Since),
            Row(Guid.NewGuid(), To, FollowStatuses.Requested, Since.AddSeconds(9)),
            Row(Guid.NewGuid(), other, FollowStatuses.Accepted, Since.AddSeconds(3)),
        };
        var groups = FollowEffectsWork.OpenGroups(batch);
        Assert.Equal(3, groups.Count);
        Assert.Equal(Since, groups.Single(g => g.ReceiverId == To && g.Kind == FollowStatuses.Accepted).Since);
        Assert.Equal(Since.AddSeconds(9), groups.Single(g => g.ReceiverId == To && g.Kind == FollowStatuses.Requested).Since);
        Assert.Single(groups, g => g.ReceiverId == other);
    }

    [Fact]
    public void No_push_when_nobody_is_left_or_follows_are_off()
    {
        Assert.Null(FollowEffectsWork.Push(Due(FollowStatuses.Accepted, 0, notify: true)));
        Assert.Null(FollowEffectsWork.Push(Due(FollowStatuses.Accepted, 3, notify: false)));
        Assert.Null(FollowEffectsWork.Push(new FollowEffectsWork.DueGroup { ReceiverId = To, Kind = "A", Count = 2, Notify = true, PendingSince = null }));
        Assert.Equal("Rahul and 2 others followed you", FollowEffectsWork.Push(Due(FollowStatuses.Accepted, 3, notify: true))!.Body);
        Assert.Equal(NotificationTypes.SocialFollowRequest, FollowEffectsWork.Push(Due("R ", 1, notify: true))!.PushKind);
    }

    [Fact]
    public void Job_sql_claims_with_skip_locked_and_counts_only_done_effects()
    {
        Assert.Contains("FOR UPDATE SKIP LOCKED", FollowEffectsWork.ClaimSql);
        Assert.Contains("\"EffectsAt\" IS NULL", FollowEffectsWork.ClaimSql);
        Assert.Contains("FOR UPDATE SKIP LOCKED", FollowEffectsWork.PickDirtySql);
        Assert.Contains("FOR UPDATE SKIP LOCKED", FollowEffectsWork.FlushSql);
        Assert.Contains("\"EffectsAt\" IS NOT NULL", FollowEffectsWork.FlushSql);
        Assert.Contains("\"AcceptedAt\" = f.\"CreatedAt\"", FollowEffectsWork.FlushSql);
        Assert.Contains("\"UserBlocks\"", FollowEffectsWork.FlushSql);
        Assert.Contains("ON CONFLICT (\"Key\") DO NOTHING", RentNearBy.Infrastructure.Services.Buddy.Be4.NoticeWriter.PushSql);
        Assert.DoesNotContain("+ 1", FollowEffectsWork.RecountSql);
    }

    [Fact]
    public void Job_is_registered_once_in_the_buddy_feature()
    {
        var src = File.ReadAllText(Path.Combine(BuddySources.Root(), "RentNearBy.Api", "Extensions", "BuddyFeatureExtensions.cs"));
        Assert.Contains("AddHostedService<FollowEffectsJob>()", src);
        Assert.Contains("AddSingleton<IFollowEffectsSignal, FollowEffectsSignal>()", src);
        Assert.Contains("AddScoped<FollowEffectsWork>()", src);
        var social = File.ReadAllText(Path.Combine(BuddySources.Root(), "RentNearBy.Api", "Extensions", "SocialFeatureExtensions.cs"));
        Assert.DoesNotContain("FollowEffectsJob", social);
    }

    private static FollowEffectsWork.PendingFollow Row(Guid follower, Guid followee, string status, DateTime at)
        => new() { FollowerId = follower, FolloweeId = followee, Status = status, CreatedAt = at };

    private static FollowEffectsWork.DueGroup Due(string kind, int count, bool notify)
        => new() { ReceiverId = To, Kind = kind, Count = count, Notify = notify, PendingSince = Since, LatestId = Rahul, LatestName = "Rahul" };
}
