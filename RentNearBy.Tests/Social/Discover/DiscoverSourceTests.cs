using RentNearBy.Infrastructure.Services.Retention;
using RentNearBy.Infrastructure.Services.Social;
using RentNearBy.Tests.Buddy.Foundation;

namespace RentNearBy.Tests.Social.Discover;

// Source and SQL pins of the Discover backend (DESIGN 2.2, 2.3, 3, 5.2, 8, 10.1). The SQL never runs on this PC, so every privacy
// and exclusion clause is pinned here; the integration tests run it where a PostGIS database exists.
public class DiscoverSourceTests
{
    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine(new[] { BuddySources.Root() }.Concat(parts).ToArray()));

    [Fact]
    public void Every_discover_statement_applies_the_exclusions()
    {
        foreach (var sql in new[] { DiscoverService.SnapshotSql, DiscoverService.HydrateSql, DiscoverService.SimilarSql, DiscoverService.PostsSql })
        {
            Assert.Contains("FROM \"Follows\" xf WHERE xf.\"FollowerId\" = @me", sql);   // followed or requested, any status
            Assert.Contains("\"UserBlocks\"", sql);                                       // blocked either way
            Assert.Contains("FROM \"DiscoverHides\" xh WHERE xh.\"UserId\" = @me", sql);  // hidden < 30 days
            Assert.Contains("\"RoleId\"", sql);                                           // active seeker only
        }
        foreach (var sql in new[] { DiscoverService.SnapshotSql, DiscoverService.HydrateSql, DiscoverService.SimilarSql })
            Assert.Contains("JOIN \"UserProfiles\" p ON p.\"UserId\"", sql);              // a profile is required
    }

    [Fact]
    public void Public_only_sources_filter_private_profiles()
    {
        var snap = DiscoverService.SnapshotSql;
        Assert.Contains("near AS (", snap);
        Assert.Contains("AND NOT p.\"IsPrivate\" AND p.\"UserId\" <> @me", snap);       // near
        Assert.Contains("WHERE p.\"UserId\" <> @me AND NOT p.\"IsPrivate\"", snap);     // interests / places
        Assert.Contains("AND NOT op.\"IsPrivate\"", DiscoverService.PostsSql);           // sameTrip
        Assert.Contains("AND NOT op.\"IsPrivate\"", DiscoverService.SimilarSql);         // post-city owners
        Assert.Contains("NOT p.\"IsPrivate\" AND p.\"Interests\" && him.\"Interests\"", DiscoverService.SimilarSql);
        Assert.Contains("(NOT p.\"IsPrivate\" OR EXISTS", DiscoverService.SimilarSql);   // the viewed person must be visible
    }

    [Fact]
    public void Near_ignores_the_notify_switch_and_needs_a_fresh_district_on_both_sides()
    {
        var snap = DiscoverService.SnapshotSql;
        Assert.DoesNotContain("NotifyNearbyTrips", snap);
        Assert.Contains("CASE WHEN p.\"LastDistrictAt\" > now() -", snap);
        Assert.Contains("p.\"LastDistrictId\" = me.district AND p.\"LastDistrictAt\" > now() -", snap);
    }

    [Fact]
    public void Mutual_middle_person_is_active_not_blocked_and_his_follow_accepted()
    {
        Assert.Contains("JOIN \"Users\" mu ON mu.\"Id\" = f1.mid", DiscoverService.SnapshotSql);
        Assert.Contains("NOT EXISTS (SELECT 1 FROM \"UserBlocks\" ub WHERE (ub.\"BlockerId\" = @me AND ub.\"BlockedId\" = f1.mid)", DiscoverService.SnapshotSql);
        Assert.Contains("JOIN \"Users\" mu ON mu.\"Id\" = a.\"FolloweeId\"", DiscoverService.HydrateSql);
        Assert.Contains("ub.\"BlockedId\" = a.\"FolloweeId\"", DiscoverService.HydrateSql);
        Assert.Contains("b.\"Status\" = 'A'", DiscoverService.HydrateSql);
    }

    [Fact]
    public void Sources_are_capped_and_interests_are_bounded_by_state_or_district()
    {
        var snap = DiscoverService.SnapshotSql;
        foreach (var cap in new[] { "LIMIT 200),", "LIMIT 400),", "LIMIT 300),", "LIMIT 100)" }) Assert.Contains(cap, snap);
        Assert.Contains("p.\"HomeStateName\" = me.state", snap);
        Assert.Contains("ORDER BY md5(@me::text || c.id::text)", snap);
        Assert.Contains("LIMIT 6", DiscoverService.MyTripsSql);
        Assert.Contains("UNION ALL", DiscoverService.MyTripsSql);                         // owned trips and joined trips
    }

    [Fact]
    public void Tag_sources_are_bounded_before_the_md5_rotation()
    {
        var bound = $"LIMIT {DiscoverService.TagCandidatesMax}) c, ";
        foreach (var sql in new[] { DiscoverService.SnapshotSql, DiscoverService.SimilarSql })
        {
            var cap = sql.IndexOf(bound, StringComparison.Ordinal);
            Assert.True(cap > 0);
            Assert.True(cap < sql.IndexOf("ORDER BY md5(@me::text || c.id::text)", StringComparison.Ordinal));
            Assert.DoesNotContain("ORDER BY md5(@me::text || p.\"UserId\"::text)", sql);
        }
        Assert.Equal(2000, DiscoverService.TagCandidatesMax);
    }

    [Fact]
    public void Similar_post_owners_are_capped_per_city_before_the_distinct_sort()
    {
        var sql = DiscoverService.SimilarSql;
        Assert.Contains("FROM cities c\n", sql.Replace("\r\n", "\n"));
        Assert.Contains("CROSS JOIN LATERAL (SELECT t.\"OwnerUserId\" AS id", sql);
        Assert.Contains("WHERE t.\"CityKey\" = c.\"CityKey\"", sql);
        Assert.Contains($"ORDER BY t.\"PublishedAt\" DESC LIMIT {DiscoverService.SimilarPostsPerCity}) x", sql);
        Assert.DoesNotContain("JOIN cities c ON c.\"CityKey\" = t.\"CityKey\"", sql);
        Assert.Equal(50, DiscoverService.SimilarPostsPerCity);
    }

    [Fact]
    public void Snapshot_stamp_carries_the_row_count_and_a_partly_evicted_snapshot_is_a_miss()
    {
        Assert.Equal("1700000000000|42", DiscoverCache.StampValue(1700000000000, 42));
        Assert.Equal("5|0", DiscoverCache.StampValue(5, 0));
        var lua = DiscoverCache.ReadScript;
        Assert.Contains("local bar = string.find(v, '|', 1, true)", lua);
        Assert.Contains("if not bar then return false end", lua);
        Assert.Contains("if n > 0 and (redis.call('EXISTS', KEYS[1]) == 0 or redis.call('EXISTS', KEYS[2]) == 0) then return false end", lua);
        Assert.Contains("local s = string.sub(v, 1, bar - 1)", lua);
        Assert.Contains("local out = {s, t}", lua);
        var src = Read("RentNearBy.Infrastructure", "Services", "Social", "DiscoverCache.cs");
        Assert.Contains("tx.StringSetAsync(s, StampValue(stamp, rows.Count), ttl)", src);
    }

    [Fact]
    public void Hide_is_idempotent_and_cannot_fail_on_self_or_an_unknown_id()
    {
        var sql = DiscoverService.HideSql;
        Assert.Contains("WHERE @him <> @me AND EXISTS (SELECT 1 FROM \"Users\" WHERE \"Id\" = @him)", sql);
        Assert.Contains("ON CONFLICT (\"UserId\", \"HiddenUserId\") DO UPDATE SET \"HiddenAt\" = now()", sql);
    }

    [Fact]
    public void Hidden_people_come_back_after_thirty_days_by_retention()
    {
        var rule = Assert.Single(RetentionRuleRegistration.Discover(typeof(RetentionRunner).Assembly).OfType<DeclarativeRetentionRule>(),
            r => r.Name == "DiscoverHides");
        Assert.Equal(30, rule.DefaultDays);
        var sql = rule.BuildDeleteSql();
        Assert.Contains("(t.\"UserId\", t.\"HiddenUserId\") IN (SELECT t.\"UserId\", t.\"HiddenUserId\"", sql);
        Assert.Contains("t.\"HiddenAt\" < @cutoff", sql);
    }

    [Fact]
    public void Going_public_marks_counts_dirty_instead_of_writing_them()
    {
        var sql = SocialProfileService.OpenRequestsSql;
        Assert.DoesNotContain("FollowerCount", sql);
        Assert.DoesNotContain("FollowingCount", sql);
        Assert.Contains("INSERT INTO \"FollowCountDirty\"", sql);
        Assert.Contains("ON CONFLICT (\"UserId\") DO UPDATE SET \"MarkedAt\" = EXCLUDED.\"MarkedAt\"", sql);
        Assert.Contains("\"EffectsAt\" = COALESCE(\"EffectsAt\", now())", sql);
        Assert.Contains("SELECT \"FollowerId\" AS \"Value\" FROM a", sql);
        Assert.Contains("\"LastDistrictId\" IS DISTINCT FROM @district", SocialProfileService.AreaSql);
        Assert.Contains("AS \"Moved\"", SocialProfileService.AreaSql);
    }

    [Fact]
    public void Handler_checks_near_me_only_for_the_near_chip_and_uses_the_discover_cursor()
    {
        var src = Read("RentNearBy.Api", "Handlers", "Social", "DiscoverHandlers.cs");
        Assert.Contains("if (picked == DiscoverChips.Near && !(await seekerLimits.GetAsync(me, ct)).NearMe)", src);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(src, "seekerLimits\\.GetAsync"));
        Assert.Contains("DiscoverCursor.TryDecode(cursor", src);
        Assert.Contains("SocialRateLimits.Write(", src);
        Assert.DoesNotContain("NotBuilt", src);
    }

    [Fact]
    public void Snapshot_write_deletes_first_and_the_lock_is_compare_and_delete()
    {
        var src = Read("RentNearBy.Infrastructure", "Services", "Social", "DiscoverCache.cs");
        var del = src.IndexOf("tx.KeyDeleteAsync([z, r, s])", StringComparison.Ordinal);
        Assert.True(del > 0);
        Assert.True(del < src.IndexOf("tx.SortedSetAddAsync", StringComparison.Ordinal));
        Assert.True(del < src.IndexOf("tx.HashSetAsync", StringComparison.Ordinal));
        Assert.Contains("if redis.call('GET', KEYS[1]) == ARGV[1] then return redis.call('DEL', KEYS[1]) end", src);
        Assert.Contains("'ZREVRANGEBYSCORE', KEYS[1], ARGV[1], '-inf', 'WITHSCORES'", src);
        Assert.Contains("CommandFlags.FireAndForget", src);
        Assert.Contains("When.NotExists", src);
    }

    [Fact]
    public void Own_trips_mates_area_interests_and_blocks_invalidate_the_snapshot()
    {
        Assert.Contains("discover?.Invalidate(me);", Read("RentNearBy.Infrastructure", "Services", "Social", "PublicTripService.cs"));
        var profile = Read("RentNearBy.Infrastructure", "Services", "Social", "SocialProfileService.cs");
        Assert.Contains("if (row.Moved) discover?.Invalidate(me);", profile);
        Assert.Contains("if (r.Interests != null || r.Places != null || r.HomeStateName != null) discover?.Invalidate(me);", profile);
        Assert.Contains("discover?.Invalidate(me, him);", Read("RentNearBy.Infrastructure", "Services", "Social", "JoinRequestService.cs"));
        Assert.Contains("discover?.Invalidate(callerId, owner);", Read("RentNearBy.Infrastructure", "Services", "Buddy", "Be2", "TripInviteService.cs"));
        var members = Read("RentNearBy.Infrastructure", "Services", "Buddy", "Be2", "TripMemberService.cs");
        Assert.Contains("discover?.Invalidate(ownerId, userId);", members);
        Assert.Contains("discover?.Invalidate(callerId, ownerId);", members);
        var trips = Read("RentNearBy.Api", "Handlers", "TripHandlers.cs");
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(trips, "discover\\?\\.Invalidate\\(userId\\)").Count);
        Assert.Contains("discover?.Invalidate(me);", Read("RentNearBy.Api", "Handlers", "Buddy", "Be1", "UserBlockHandlers.cs"));
    }

    [Fact]
    public void Discover_services_are_registered_in_the_be1_registration()
    {
        var reg = Read("RentNearBy.Api", "Extensions", "Buddy", "Be1Registration.cs");
        Assert.Contains("services.AddSingleton(sp => new DiscoverCache(sp.GetService<IConnectionMultiplexer>()", reg);
        Assert.Contains("services.AddScoped<IDiscoverService, DiscoverService>();", reg);
    }
}
