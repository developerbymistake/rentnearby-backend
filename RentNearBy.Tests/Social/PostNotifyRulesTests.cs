using System.Text.Json;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Services.Buddy.Be4;

namespace RentNearBy.Tests.Social;

// Post notify (owner 2026-11-27, Buddy_resume/post_notify/SPEC.md): the pure rules and the push copy. Use cases 3, 9, 11, 12,
// 13, 14, 25-29 (caps), 32 (one place for the numbers).
public class PostNotifyRulesTests
{
    private static readonly DateTime Now = new(2026, 11, 27, 6, 0, 0, DateTimeKind.Utc); // 11:30 IST
    private static readonly DateOnly Today = new(2026, 11, 27);
    private static readonly Guid User = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly Guid Poster = Guid.Parse("33333333-3333-4333-8333-333333333333");
    private static readonly Guid Trip = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");

    private static string N(Guid id) => id.ToString("N");

    // ------------------------------------------------------------------ numbers in one place

    [Fact]
    public void The_caps_are_the_owner_numbers_and_live_in_social_limits()
    {
        Assert.True(PostNotifyRules.FollowersImmediate);
        Assert.Equal((2, 1, 4, 50, 200, 60), (SocialLimits.NotifiedPostsPerWeek, SocialLimits.NotifiedPostsFirstDay, SocialLimits.PublishesPerWeek,
            SocialLimits.PendingRequestsMax, SocialLimits.NearbyPerPostMax, SocialLimits.NearbyStartWithinDays));
        Assert.Equal(150, PostNotifyRules.NearbyKm);
        Assert.Equal(2000, SocialLimits.FollowedPostedMax);
        Assert.Equal(TimeSpan.FromDays(7), PostNotifyRules.Window);
        Assert.Equal(TimeSpan.FromDays(30), PostNotifyRules.AreaStaleAfter);
        Assert.Equal(new TimeOnly(7, 0), PostNotifyRules.DigestAtIst);
        Assert.Equal(new TimeOnly(21, 0), PostNotifyRules.DigestLastIst);
    }

    // ------------------------------------------------------------------ rolling 7-day caps (use cases 3, 25-27, 31)

    [Fact]
    public void A_new_account_may_notify_once_on_its_first_day_then_twice_a_week()
    {
        Assert.Equal(1, PostNotifyRules.NotifyLimit(Now.AddHours(-23), Now));
        Assert.Equal(2, PostNotifyRules.NotifyLimit(Now.AddHours(-24), Now));
        Assert.Equal(2, PostNotifyRules.NotifyLimit(Now.AddDays(-400), Now));
    }

    [Fact]
    public void The_window_is_rolling_and_keeps_only_recent_times_oldest_first()
    {
        var times = new[] { Now.AddDays(-1), Now.AddDays(-8), Now.AddDays(-6.9), Now.AddDays(-7) };
        Assert.Equal([Now.AddDays(-6.9), Now.AddDays(-1)], PostNotifyRules.InWindow(times, Now));
        Assert.Empty(PostNotifyRules.InWindow(null, Now));
        Assert.Equal([Now.AddDays(-1), Now], PostNotifyRules.Append([Now.AddDays(-9), Now.AddDays(-1)], Now));
    }

    [Fact]
    public void Next_allowed_is_when_the_oldest_use_that_blocks_leaves_the_window()
    {
        Assert.Null(PostNotifyRules.NextAllowedAt([Now.AddDays(-2)], 2));
        Assert.Equal(Now.AddDays(-2) + PostNotifyRules.Window, PostNotifyRules.NextAllowedAt([Now.AddDays(-2), Now.AddDays(-1)], 2));
        // Four publishes: the 5th is allowed when the first of the four leaves the window.
        var four = new[] { Now.AddDays(-6), Now.AddDays(-3), Now.AddDays(-2), Now.AddHours(-1) };
        Assert.Equal(Now.AddDays(1), PostNotifyRules.NextAllowedAt(four, PostNotifyRules.PublishesPerWeek));
        // A limit lowered below the use count (new account) waits for the right entry.
        Assert.Equal(Now.AddDays(-1) + PostNotifyRules.Window, PostNotifyRules.NextAllowedAt([Now.AddDays(-2), Now.AddDays(-1)], 1));
        Assert.Equal(DateTimeKind.Utc, PostNotifyRules.NextAllowedAt([Now], 1)!.Value.Kind);
    }

    // ------------------------------------------------------------------ nearby toggle (use cases 1-3, 8)

    [Theory]
    [InlineData(false, true, false, null, PostNotifyStates.Off)]
    [InlineData(true, true, false, null, PostNotifyStates.Queued)]
    [InlineData(true, false, false, null, PostNotifyStates.WeekLimit)]
    [InlineData(true, true, true, null, PostNotifyStates.Private)]
    [InlineData(true, false, true, null, PostNotifyStates.Private)]
    [InlineData(true, true, false, 60, PostNotifyStates.Queued)]
    [InlineData(true, true, false, 61, PostNotifyStates.TooFar)]
    public void The_nearby_toggle_answer(bool requested, bool canNotify, bool posterPrivate, int? startInDays, string expected)
        => Assert.Equal(expected, PostNotifyRules.NearbyState(requested, canNotify, posterPrivate,
            startInDays is { } d ? Today.AddDays(d) : null, Today));

    [Fact]
    public void Nearby_only_for_trips_starting_within_60_days()
    {
        Assert.True(PostNotifyRules.StartsSoonEnough(null, Today));
        Assert.True(PostNotifyRules.StartsSoonEnough(Today, Today));
        Assert.True(PostNotifyRules.StartsSoonEnough(Today.AddDays(60), Today));
        Assert.False(PostNotifyRules.StartsSoonEnough(Today.AddDays(61), Today));
    }

    // ------------------------------------------------------------------ bump protection (use case 28)

    [Fact]
    public void A_re_publish_inside_7_days_keeps_its_feed_place()
    {
        Assert.Equal(Now, PostNotifyRules.PublishedAtFor(null, Now));
        Assert.Equal(Now.AddDays(-6), PostNotifyRules.PublishedAtFor(Now.AddDays(-6), Now));
        Assert.Equal(Now, PostNotifyRules.PublishedAtFor(Now.AddDays(-7), Now));
    }

    // ------------------------------------------------------------------ area (use cases 11, 18)

    [Fact]
    public void An_area_older_than_30_days_or_missing_is_stale()
    {
        Assert.False(PostNotifyRules.AreaFresh(null, Now));
        Assert.True(PostNotifyRules.AreaFresh(Now.AddDays(-30), Now));
        Assert.False(PostNotifyRules.AreaFresh(Now.AddDays(-30).AddMinutes(-1), Now));
    }

    [Fact]
    public void The_area_is_written_only_on_change_or_after_6_hours()
    {
        var d = Guid.NewGuid();
        Assert.True(PostNotifyRules.AreaNeedsWrite(null, null, d, Now));
        Assert.True(PostNotifyRules.AreaNeedsWrite(Guid.NewGuid(), Now, d, Now));
        Assert.False(PostNotifyRules.AreaNeedsWrite(d, Now.AddHours(-5), d, Now));
        Assert.True(PostNotifyRules.AreaNeedsWrite(d, Now.AddHours(-6), d, Now));
    }

    // ------------------------------------------------------------------ digest timing (use case 13)

    [Fact]
    public void The_digest_cutoff_is_07_00_ist_and_it_sends_only_until_21_00_ist()
    {
        Assert.Equal(new DateTime(2026, 11, 27, 1, 30, 0, DateTimeKind.Utc), PostNotifyRules.DigestCutoffUtc(Today));
        Assert.False(PostNotifyRules.DigestMaySend(new DateTime(2026, 11, 27, 1, 29, 0, DateTimeKind.Utc))); // 06:59 IST
        Assert.True(PostNotifyRules.DigestMaySend(new DateTime(2026, 11, 27, 1, 30, 0, DateTimeKind.Utc)));  // 07:00 IST
        Assert.True(PostNotifyRules.DigestMaySend(new DateTime(2026, 11, 27, 15, 29, 0, DateTimeKind.Utc))); // 20:59 IST
        Assert.False(PostNotifyRules.DigestMaySend(new DateTime(2026, 11, 27, 15, 30, 0, DateTimeKind.Utc))); // 21:00 IST
    }

    // ------------------------------------------------------------------ push copy (use cases 5, 12, 13, 15, 22)

    private static OutboxPushPayload Parse(string json) => JsonSerializer.Deserialize<OutboxPushPayload>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    [Fact]
    public void Follower_push_names_only_the_poster_and_opens_the_post()
    {
        var p = SocialPushCopy.TripPosted(User, Poster, Trip, "Kavya", Now);
        Assert.Equal(("New trip", "Kavya posted a trip"), (p.Title, p.Body));
        Assert.Equal(UserNotificationRoutes.TripPost, p.Route);
        Assert.Equal($$"""{"tripId":"{{Trip}}"}""", p.ArgsJson);
        Assert.Equal($"tpost:{N(Poster)}", p.Tag);
        Assert.Equal(NotificationTypes.SocialTripPosted, p.PushKind);
        Assert.Equal("social_trip_posted", NotificationTypes.ToWireValue(p.PushKind));
        Assert.Equal(TimeSpan.FromHours(12), p.Expires);
        Assert.Equal("Someone posted a trip", SocialPushCopy.TripPosted(User, Poster, Trip, "  ", Now).Body);
    }

    [Fact]
    public void Follower_push_rings_once_per_poster_per_hour()
    {
        var a = SocialPushCopy.TripPosted(User, Poster, Trip, "Kavya", Now);
        var b = SocialPushCopy.TripPosted(User, Poster, Guid.NewGuid(), "Kavya", Now.AddMinutes(30));
        var c = SocialPushCopy.TripPosted(User, Poster, Trip, "Kavya", Now.AddMinutes(60));
        Assert.Equal(a.Key, b.Key);
        Assert.NotEqual(a.Key, c.Key);
        Assert.NotEqual(a.Key, SocialPushCopy.TripPosted(Guid.NewGuid(), Poster, Trip, "Kavya", Now).Key);
    }

    [Fact]
    public void Digest_names_the_poster_for_one_trip_and_counts_several()
    {
        var one = SocialPushCopy.NearbyDigest(User, 1, Trip, "Kavya", Today);
        Assert.Equal(("Trip near you", "Kavya posted a trip near you"), (one.Title, one.Body));
        Assert.Equal(UserNotificationRoutes.TripPost, one.Route);
        Assert.Contains(Trip.ToString(), one.ArgsJson);
        var many = SocialPushCopy.NearbyDigest(User, 3, Trip, "Kavya", Today);
        Assert.Equal(("Trips near you", "3 new trips near you"), (many.Title, many.Body));
        Assert.Equal(UserNotificationRoutes.TravelBuddies, many.Route);
        Assert.Equal("{}", many.ArgsJson);
        // One key per receiver per IST day, whatever the count: a second run never rings again.
        Assert.Equal($"nearby:{N(User)}:20261127", one.Key);
        Assert.Equal(one.Key, many.Key);
        Assert.NotEqual(one.Key, SocialPushCopy.NearbyDigest(User, 1, Trip, "Kavya", Today.AddDays(1)).Key);
        Assert.Equal("social_nearby_trips", NotificationTypes.ToWireValue(one.PushKind));
        Assert.Equal(Now.AddHours(14), Parse(NoticeWriter.Payload(one, Now)).ExpiresAtUtc);
    }

    [Fact]
    public void Lock_screen_copy_never_carries_a_city_date_or_post_text()
    {
        var bodies = new[]
        {
            SocialPushCopy.TripPosted(User, Poster, Trip, "Kavya", Now).Body,
            SocialPushCopy.NearbyDigest(User, 1, Trip, "Kavya", Today).Body,
            SocialPushCopy.NearbyDigest(User, 2, Trip, "Kavya", Today).Body,
        };
        Assert.Equal(["Kavya posted a trip", "Kavya posted a trip near you", "2 new trips near you"], bodies);
    }

    [Fact]
    public void The_full_post_push_tells_the_host_once_and_replaces_his_request_pushes()
    {
        var p = SocialPushCopy.JoinRequestsFull(Poster, Trip);
        Assert.Equal(("Join requests", "You have 50 requests. Review them."), (p.Title, p.Body));
        Assert.Equal($"reqfull:{N(Trip)}", p.Key);
        Assert.Equal(SocialPushCopy.JoinRequest(Poster, Guid.NewGuid(), Trip, "Aman").Tag, p.Tag);
        Assert.Equal(UserNotificationRoutes.JoinRequests, p.Route);
        Assert.Equal("social_join_requests_full", NotificationTypes.ToWireValue(p.PushKind));
    }

    // ------------------------------------------------------------------ typed errors (use cases 15, 16, 25)

    [Fact]
    public void New_errors_have_calm_text_and_the_right_status()
    {
        Assert.Equal((409, "This trip has enough requests for now."),
            (SocialErrorTypes.StatusOf(SocialErrorTypes.TripRequestsFull), SocialErrorTypes.MessageOf(SocialErrorTypes.TripRequestsFull)));
        Assert.Equal((409, "You can no longer join this trip."),
            (SocialErrorTypes.StatusOf(SocialErrorTypes.CannotRejoin), SocialErrorTypes.MessageOf(SocialErrorTypes.CannotRejoin)));
        Assert.Equal(429, SocialErrorTypes.StatusOf(SocialErrorTypes.PublishLimit));
        Assert.Equal(400, SocialErrorTypes.StatusOf(SocialErrorTypes.InvalidArea));
        // Not a monthly plan cap: no {resetsAt, canUpgrade} body and no Plus prompt.
        Assert.False(SocialErrorTypes.IsCap(SocialErrorTypes.PublishLimit));
    }
}
