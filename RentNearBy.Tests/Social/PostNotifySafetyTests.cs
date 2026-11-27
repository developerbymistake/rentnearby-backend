using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Services.Social;
using RentNearBy.Tests.Buddy.Foundation;

namespace RentNearBy.Tests.Social;

// Safety review of post notify (Buddy_resume/post_notify/review_safety.md, 2026-11-27). Always run (no database); the
// end-to-end cases live in PostNotifyIntegrationTests (safety review section).
public class PostNotifySafetyTests
{
    private static string Src(params string[] parts) => File.ReadAllText(Path.Combine([BuddySources.Root(), .. parts]));

    // S1: a Men only / Women only post reaches nearby strangers only when the poster is of that gender.
    [Theory]
    [InlineData(JoinGenders.Anyone, SocialGenders.Male, true)]
    [InlineData(JoinGenders.Anyone, SocialGenders.Other, true)]
    [InlineData(JoinGenders.Anyone, null, true)]
    [InlineData(JoinGenders.Women, SocialGenders.Female, true)]
    [InlineData(JoinGenders.Women, SocialGenders.Male, false)]
    [InlineData(JoinGenders.Women, SocialGenders.Other, false)]
    [InlineData(JoinGenders.Women, null, false)]
    [InlineData(JoinGenders.Men, SocialGenders.Male, true)]
    [InlineData(JoinGenders.Men, SocialGenders.Female, false)]
    [InlineData(JoinGenders.Men, SocialGenders.Other, false)]
    public void Gender_only_posts_reach_strangers_only_from_a_poster_of_that_gender(string joinGender, string? posterGender, bool expected)
        => Assert.Equal(expected, PostNotifyRules.PosterFitsAudience(joinGender, posterGender));

    [Fact]
    public void The_gender_rule_is_applied_at_publish_and_again_at_send_time()
    {
        var notifier = Src("RentNearBy.Infrastructure", "Services", "Social", "PostNotifier.cs");
        Assert.Contains("p.\"IsPrivate\", p.\"Gender\",", PostNotifier.PosterSql);
        var state = notifier.IndexOf("PostNotifyRules.NearbyState(", StringComparison.Ordinal);
        var gender = notifier.IndexOf("PostNotifyRules.PosterFitsAudience(trip.JoinGender, poster.Gender)", StringComparison.Ordinal);
        Assert.True(state > 0 && gender > state);
        Assert.True(gender < notifier.IndexOf("trip.NotifyNearby = nearby == PostNotifyStates.Queued", StringComparison.Ordinal));
        Assert.Equal("gender", PostNotifyStates.Gender);
        // Re-checked in the digest: an edit of "who can join" after publish cannot open the path.
        Assert.Contains($"t.\"JoinGender\" = '{JoinGenders.Women}' AND op.\"Gender\" = '{SocialGenders.Female}'", NearbyDigestJob.DigestSql);
        Assert.Contains($"t.\"JoinGender\" = '{JoinGenders.Men}' AND op.\"Gender\" = '{SocialGenders.Male}'", NearbyDigestJob.DigestSql);
    }

    // S2: a double tap on publish (two parallel PUT /post) is one publish: the stored IsPublic is re-read under the lock.
    [Fact]
    public void A_parallel_publish_is_re_checked_under_the_poster_lock_before_any_cap_is_charged()
    {
        var src = Src("RentNearBy.Infrastructure", "Services", "Social", "PublicTripService.cs");
        var put = src[src.IndexOf("public async Task<BuddyResult<PublicTripDto>> PutPostAsync", StringComparison.Ordinal)..];
        var lockAt = put.IndexOf("BuddyLocks.LockTripSlotsAsync(db, me, ct)", StringComparison.Ordinal);
        var recheck = put.IndexOf("if (await PublishedMeanwhileAsync(tripId, ct)) firstPublish = false;", StringComparison.Ordinal);
        Assert.True(lockAt > 0 && recheck > lockAt);
        Assert.True(recheck < put.IndexOf("PostNotifier.ReadAsync", StringComparison.Ordinal));
        Assert.True(recheck < put.IndexOf("LivePostsAsync", StringComparison.Ordinal));
        Assert.Contains("db.Trips.AsNoTracking().Where(t => t.Id == tripId).Select(t => t.IsPublic)", src);
    }

    // S3: travellers of one district share its centroid distance; the 200 per post must not always be the same low user ids.
    [Fact]
    public void Ties_inside_a_district_are_spread_per_post_not_by_user_id()
    {
        var sql = NearbyDigestJob.DigestSql;
        Assert.Contains("ORDER BY n.dist, md5(pr.\"UserId\"::text || n.trip_id::text)", sql);
        Assert.DoesNotContain("ORDER BY n.dist, pr.\"UserId\")", sql);
    }

    // S4: a post that could not be sent within 7 days is closed (no stale "new trip", no ever-growing pending set), and the big
    // morning statement has its own timeout (a failed run marks the day done).
    [Fact]
    public void Stale_pending_posts_are_closed_and_the_digest_has_a_long_timeout()
    {
        Assert.Equal(TimeSpan.FromDays(7), PostNotifyRules.NearbyPendingMaxAge);
        var sql = NearbyDigestJob.DigestSql;
        Assert.Contains("COALESCE(t.\"LastPublishedAt\", t.\"PublishedAt\") < @cutoff AND COALESCE(t.\"LastPublishedAt\", t.\"PublishedAt\") >= @stale", sql);
        Assert.Contains("WHERE t.\"NotifyNearby\" AND t.\"NearbyNotifiedAt\" IS NULL AND COALESCE(t.\"LastPublishedAt\", t.\"PublishedAt\") < @stale", sql);
        // The age counts from the latest publish: bump protection keeps PublishedAt on a re-publish (verifier 2026-11-27).
        Assert.DoesNotContain("t.\"PublishedAt\" < @stale", sql);
        Assert.DoesNotContain("t.\"PublishedAt\" < @cutoff", sql);
        var job = Src("RentNearBy.Infrastructure", "Services", "Social", "NearbyDigestJob.cs");
        Assert.Contains("At(\"stale\", cutoff - PostNotifyRules.NearbyPendingMaxAge)", job);
        Assert.True(job.IndexOf("db.Database.SetCommandTimeout(PostNotifyRules.DigestCommandTimeout);", StringComparison.Ordinal)
                    < job.IndexOf("BeginTransactionAsync", StringComparison.Ordinal));
        Assert.True(PostNotifyRules.DigestCommandTimeout >= TimeSpan.FromMinutes(2));
    }

    // Lock-screen rule: no push of this feature carries a city, a date or the post text.
    [Fact]
    public void Pushes_name_at_most_the_poster()
    {
        var u = Guid.NewGuid();
        var trip = Guid.NewGuid();
        var now = new DateTime(2026, 11, 27, 6, 0, 0, DateTimeKind.Utc);
        var pushes = new[]
        {
            SocialPushCopy.TripPosted(u, Guid.NewGuid(), trip, "Kavya", now),
            SocialPushCopy.NearbyDigest(u, 1, trip, "Kavya", DateOnly.FromDateTime(now)),
            SocialPushCopy.NearbyDigest(u, 3, trip, "Kavya", DateOnly.FromDateTime(now)),
            SocialPushCopy.JoinRequestsFull(u, trip),
        };
        foreach (var p in pushes) // batch-loop: 4 fixed pushes
        {
            var text = p.Title + " " + p.Body;
            Assert.DoesNotContain("Nainital", text);
            Assert.DoesNotContain("2026", text);
            Assert.DoesNotContain("Dec", text);
        }
    }
}
