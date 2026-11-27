using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using RentNearBy.Core.DTOs.Social;
using RentNearBy.Core.Entities.Social;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.Social;
using RentNearBy.Tests.Buddy.Foundation;

namespace RentNearBy.Tests.Social;

// Post notify (owner 2026-11-27): source and model guards that always run (no database). Each SQL rule of SPEC is pinned on
// the statement that enforces it, so a later edit that drops one fails here (use cases 4, 6-11, 14, 15, 19, 23, 24).
public class PostNotifyGuardTests
{
    private static string Src(params string[] parts) => File.ReadAllText(Path.Combine([BuddySources.Root(), .. parts]));

    private static IModel Model()
    {
        using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql("Host=localhost;Database=design_only", o => o.UseNetTopologySuite())
            .Options);
        return db.GetService<IDesignTimeModel>().Model;
    }

    // ------------------------------------------------------------------ followers push (use cases 1, 4, 5, 6, 7, 9)

    [Fact]
    public void Followers_statement_keeps_every_receiver_rule()
    {
        var sql = PostNotifier.FollowersSql;
        Assert.Contains("p.\"NotifyFollowedTrips\"", sql);                       // switch gates bell row AND push (push only to RETURNING rows)
        Assert.Contains(SocialSql.ActiveSeeker("u"), sql);                        // deleted / disabled / non-Seeker never
        Assert.Contains($"f.\"Status\" = '{FollowStatuses.Accepted}'", sql);
        Assert.Contains($"p.\"Gender\" = '{SocialGenders.Male}'", sql);           // Men only -> men
        Assert.Contains($"p.\"Gender\" = '{SocialGenders.Female}'", sql);         // Women only -> women
        Assert.Contains("\"UserBlocks\"", sql);                                   // blocked either way
        Assert.Contains($"LIMIT {SocialLimits.FollowedPostedMax}", sql);
        Assert.Contains("ON CONFLICT (\"UserId\", \"Kind\", \"TripId\") DO NOTHING", sql); // never twice per follower and trip
        Assert.Contains("RETURNING \"UserId\"", sql);
    }

    [Fact]
    public void Publish_notifies_inside_the_transaction_and_only_on_a_publishing_call()
    {
        var src = Src("RentNearBy.Infrastructure", "Services", "Social", "PublicTripService.cs");
        Assert.DoesNotContain("FollowedPostedSql", src);
        var create = src[src.IndexOf("public async Task<BuddyResult<PublicTripDto>> CreateAsync", StringComparison.Ordinal)..];
        create = create[..create.IndexOf("private async Task<BuddyResult<PublicTripDto>> AnswerAsync", StringComparison.Ordinal)];
        Assert.True(create.IndexOf("LockTripSlotsAsync", StringComparison.Ordinal) < create.IndexOf("PublishRefusal", StringComparison.Ordinal));
        Assert.True(create.IndexOf("PostNotifier.OnPublishAsync", StringComparison.Ordinal) < create.IndexOf("CommitTransactionAsync", StringComparison.Ordinal));
        var put = src[src.IndexOf("public async Task<BuddyResult<PublicTripDto>> PutPostAsync", StringComparison.Ordinal)..];
        Assert.Contains("if (firstPublish)", put);
        Assert.Contains("poster = await PostNotifier.ReadAsync(db, me, ct);", put);
        Assert.Contains("if (poster is not null)", put);                         // an edit (not a publish) never notifies
        Assert.True(put.IndexOf("PostNotifier.OnPublishAsync", StringComparison.Ordinal) < put.IndexOf("CommitTransactionAsync", StringComparison.Ordinal));
        var notifier = Src("RentNearBy.Infrastructure", "Services", "Social", "PostNotifier.cs");
        Assert.Contains("if (trip.NotifiedAt is not null)", notifier);           // edit / re-publish never notifies again
        Assert.Contains("PostNotifyRules.FollowersImmediate", notifier);
    }

    // ------------------------------------------------------------------ nearby digest (use cases 2, 4, 6-14, 24)

    [Fact]
    public void Digest_is_one_statement_with_every_eligibility_rule()
    {
        var sql = NearbyDigestJob.DigestSql;
        Assert.Contains("NOT op.\"IsPrivate\"", sql);                            // a private poster never reaches strangers
        Assert.Contains("t.\"NotifyNearby\" AND t.\"NearbyNotifiedAt\" IS NULL", sql);
        Assert.Contains("COALESCE(t.\"LastPublishedAt\", t.\"PublishedAt\") < @cutoff", sql); // after 07:00 (of the latest publish) waits for tomorrow
        Assert.Contains(PublicTripReader.LivePost("t"), sql);                     // deleted / unpublished / ended: skipped at send time
        Assert.Contains("<= @soon", sql);                                         // starts within 60 days
        Assert.Contains($"< {SocialLimits.PendingRequestsMax}", sql);             // a Full post is skipped
        Assert.Contains("pr.\"NotifyNearbyTrips\"", sql);                         // switch
        Assert.Contains("pr.\"LastDistrictAt\" >= @fresh", sql);                  // stale area skipped
        Assert.Contains($"ST_DWithin(a.c, p.g, {PostNotifyRules.NearbyKm * 1000})", sql);
        Assert.Contains($"pr.\"Gender\" = '{SocialGenders.Male}'", sql);
        Assert.Contains($"pr.\"Gender\" = '{SocialGenders.Female}'", sql);
        Assert.Contains("\"UserBlocks\"", sql);
        Assert.Contains("NOT EXISTS (SELECT 1 FROM \"Follows\" f", sql);          // a follower is told once, by the follower push
        Assert.Contains($"'{NoticeKinds.FollowedPosted}', '{NoticeKinds.NearbyPosted}'", sql);
        Assert.Contains("pr.\"UserId\" <> p.\"OwnerUserId\"", sql);               // never the poster
        Assert.Contains("\"TripMembers\" m WHERE m.\"TripId\" = n.trip_id AND m.\"UserId\" = pr.\"UserId\"", sql);
        Assert.Contains("\"TripJoinRequests\" r WHERE r.\"TripId\" = n.trip_id AND r.\"RequesterId\" = pr.\"UserId\"", sql);
        Assert.Contains("ORDER BY n.dist", sql);                                  // nearest first
        Assert.Contains($"c.rn <= {SocialLimits.NearbyPerPostMax}", sql);
        Assert.Contains("UPDATE \"Trips\" t SET \"NearbyNotifiedAt\" = now()", sql);
        Assert.Contains("GROUP BY k.\"UserId\"", sql);                            // one push per traveller
        Assert.Contains(SocialSql.ActiveSeeker("u"), sql);
        Assert.Contains(SocialSql.ActiveSeeker("o"), sql);
    }

    [Fact]
    public void Digest_job_runs_daily_at_07_00_ist_and_pushes_in_its_transaction()
    {
        var job = new NearbyDigestJob(null!, null!, TimeProvider.System, null!);
        Assert.Equal(("social-nearby-digest", new TimeOnly(7, 0)), (job.Name, job.DailyAtIst!.Value));
        var src = Src("RentNearBy.Infrastructure", "Services", "Social", "NearbyDigestJob.cs");
        Assert.Contains("if (!PostNotifyRules.DigestMaySend(now)) return 0;", src);
        Assert.True(src.IndexOf("notices.PushAsync", StringComparison.Ordinal) < src.IndexOf("tx.CommitAsync", StringComparison.Ordinal));
        Assert.Contains("services.AddScoped<IBuddyFeatureJob, NearbyDigestJob>();", Src("RentNearBy.Api", "Extensions", "SocialFeatureExtensions.cs"));
    }

    // ------------------------------------------------------------------ requests (use cases 15, 16)

    [Fact]
    public void Join_send_checks_removed_and_the_50_cap_under_the_trip_lock()
    {
        var src = Src("RentNearBy.Infrastructure", "Services", "Social", "JoinRequestService.cs");
        var send = src[src.IndexOf("public async Task<BuddyResult<JoinSentDto>> SendAsync", StringComparison.Ordinal)..];
        var lockAt = send.IndexOf("BuddyLocks.LockTripAsync(db, tripId, c)", StringComparison.Ordinal);
        Assert.True(lockAt > 0);
        Assert.True(send.IndexOf("SocialErrorTypes.CannotRejoin", StringComparison.Ordinal) > lockAt);
        Assert.True(send.IndexOf("SocialErrorTypes.TripRequestsFull", StringComparison.Ordinal) > lockAt);
        Assert.Contains("m.\"Status\" = '{TripMemberStatuses.Removed}') AS \"Removed\"", src);
        Assert.Contains("SocialPushCopy.JoinRequestsFull", send);
    }

    [Fact]
    public void Every_trip_list_carries_requests_full()
    {
        var src = Src("RentNearBy.Infrastructure", "Services", "Social", "PublicTripReader.cs");
        Assert.Contains($">= {{SocialLimits.PendingRequestsMax}}) AS \"RequestsFull\"", src);
        Assert.Contains("RequestsFull = r.RequestsFull", src);
        Assert.NotNull(typeof(PublicTripDto).GetProperty(nameof(PublicTripDto.RequestsFull)));
    }

    [Fact]
    public void Owner_sees_the_nearby_state_others_never_and_a_removed_member_reads_removed()
    {
        var owner = Guid.NewGuid();
        var now = new DateTime(2026, 11, 27, 6, 0, 0, DateTimeKind.Utc);
        var row = new PublicTripReader.Row
        {
            TripId = Guid.NewGuid(), OwnerUserId = owner, OwnerName = "Kavya", JoinGender = JoinGenders.Anyone, PostText = "Hills.",
            PostDays = 3, Seats = 3, PublishedAt = now.AddHours(-2), PostWindowMonths = 2, IsPublic = true, Live = true,
            NotifyNearby = true, NearbyNotifiedAt = now, RequestsFull = true,
        };
        var mine = PublicTripReader.ToDto(row, owner, now, null, SocialGenders.Female);
        Assert.True(mine.NotifyNearby);
        Assert.Equal(now, mine.NearbyNotifiedAt);
        Assert.True(mine.RequestsFull);
        var other = PublicTripReader.ToDto(row, Guid.NewGuid(), now, null, SocialGenders.Male);
        Assert.Null(other.NotifyNearby);
        Assert.Null(other.NearbyNotifiedAt);
        Assert.True(other.RequestsFull);
        row.MyRemoved = true;
        Assert.Equal(MyJoinStates.Removed, PublicTripReader.ToDto(row, Guid.NewGuid(), now, null, SocialGenders.Male).MyJoin);
        row.MyMember = true;
        Assert.Equal(MyJoinStates.Member, PublicTripReader.ToDto(row, Guid.NewGuid(), now, null, SocialGenders.Male).MyJoin);
    }

    // ------------------------------------------------------------------ area and privacy (use cases 18, 23)

    [Fact]
    public void Area_stores_a_district_id_only()
    {
        Assert.Equal([nameof(UpdateAreaRequest.DistrictId)], typeof(UpdateAreaRequest).GetProperties().Select(p => p.Name));
        var profile = typeof(UserProfile).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name).ToList();
        Assert.Contains(nameof(UserProfile.LastDistrictId), profile);
        Assert.DoesNotContain(profile, n => n.Contains("Lat", StringComparison.Ordinal) || n.Contains("Lng", StringComparison.Ordinal)
                                             || n.Contains("Longitude", StringComparison.Ordinal));
        var sql = SocialProfileService.AreaSql;
        Assert.Contains("FROM \"Districts\" x WHERE x.\"Id\" = @district", sql); // unknown district = 400, nothing written
        Assert.Contains("\"LastDistrictId\" IS DISTINCT FROM @district", sql);    // throttled write
    }

    // ------------------------------------------------------------------ model, settings, deletion (use cases 6, 19, 21, 24)

    [Fact]
    public void Columns_defaults_and_indexes_exist()
    {
        var m = Model();
        var profile = m.FindEntityType(typeof(UserProfile))!;
        Assert.Equal(true, profile.FindProperty(nameof(UserProfile.NotifyNearbyTrips))!.GetDefaultValue());
        Assert.True(profile.FindProperty(nameof(UserProfile.LastDistrictId))!.IsNullable);
        Assert.True(profile.FindProperty(nameof(UserProfile.LastDistrictAt))!.IsNullable);
        Assert.False(profile.FindProperty(nameof(UserProfile.NotifiedPostTimes))!.IsNullable);
        Assert.False(profile.FindProperty(nameof(UserProfile.PublishTimes))!.IsNullable);
        var nearby = profile.GetIndexes().Single(i => i.GetDatabaseName() == "ix_userprofiles_nearby");
        Assert.Equal("\"NotifyNearbyTrips\" AND \"LastDistrictId\" IS NOT NULL", nearby.GetFilter());
        var trip = m.FindEntityType(typeof(Trip))!;
        Assert.Equal(false, trip.FindProperty(nameof(Trip.NotifyNearby))!.GetDefaultValue());
        Assert.True(trip.FindProperty(nameof(Trip.NotifiedAt))!.IsNullable);
        Assert.True(trip.FindProperty(nameof(Trip.NearbyNotifiedAt))!.IsNullable);
        Assert.True(trip.FindProperty(nameof(Trip.LastPublishedAt))!.IsNullable);   // 72-hour post lock start (PostLockRules)
        var pending = trip.GetIndexes().Single(i => i.GetDatabaseName() == "ix_trips_nearby_pending");
        Assert.Equal("\"NotifyNearby\" AND \"NearbyNotifiedAt\" IS NULL", pending.GetFilter());
    }

    [Fact]
    public void Both_switches_are_read_and_written_by_the_server()
    {
        Assert.NotNull(typeof(UpdatePrivacyRequest).GetProperty(nameof(UpdatePrivacyRequest.NotifyNearbyTrips)));
        Assert.True(new MyProfileDto().NotifyNearbyTrips);
        var src = Src("RentNearBy.Infrastructure", "Services", "Social", "SocialProfileService.cs");
        Assert.Contains("if (r.NotifyNearbyTrips is { } nn) profile.NotifyNearbyTrips = nn;", src);
        Assert.Contains("if (r.NotifyFollowedTrips is { } nt) profile.NotifyFollowedTrips = nt;", src);
        Assert.Contains("NotifyNearbyTrips = row.NotifyNearbyTrips ?? true", src);
    }

    [Fact]
    public void New_columns_go_with_the_account()
    {
        var c = AccountDeletionCoverage.Tables.ToDictionary(t => t.Table);
        Assert.Equal(DeletionModes.Cascade, c["UserProfiles"].Mode);
        Assert.True(c["Trips"].Mode.HasFlag(DeletionModes.Cascade));
        Assert.True(c["UserNotices"].Mode.HasFlag(DeletionModes.Cascade));
        Assert.Contains(NotificationTypes.SocialTripPosted, TravellerOnly.NotificationTypes);   // delivered to Seekers only
        Assert.Contains(NotificationTypes.SocialNearbyTrips, TravellerOnly.NotificationTypes);
        Assert.Contains(NoticeKinds.NearbyPosted, TravellerOnly.NoticeKinds);
    }

    [Fact]
    public void Nearby_bell_row_opens_the_post()
    {
        var trip = Guid.NewGuid();
        var route = NoticeRoutes.For(NoticeKinds.NearbyPosted, trip, null);
        Assert.Equal(UserNotificationRoutes.TripPost, route.Route);
        Assert.Equal(trip.ToString(), route.Args["tripId"]);
        Assert.True(NoticeKinds.NearbyPosted.Length <= 20);
    }
}
