using System.Text.RegularExpressions;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Services.Social;
using RentNearBy.Tests.Buddy.Foundation;

namespace RentNearBy.Tests.Social.Discover;

// Verify findings (count race + deadlock order): every dirty writer holds the dirty row lock until it commits, the recount deletes
// then counts in a separate statement, and every multi-row write of the claim locks keys in one fixed order.
public class FollowCountRaceTests
{
    private const string Upsert = "ON CONFLICT (\"UserId\") DO UPDATE SET \"MarkedAt\" = EXCLUDED.\"MarkedAt\"";

    private static string Src(params string[] parts) => File.ReadAllText(Path.Combine([BuddySources.Root(), .. parts]));

    private static string DeletionFollowsSql()
    {
        var text = Src("RentNearBy.Infrastructure", "Services", "Social", "SocialDeletionParticipant.cs");
        var a = text.IndexOf("FollowsSql = \"\"\"", StringComparison.Ordinal) + 16;
        var b = text.IndexOf("\"\"\";", a, StringComparison.Ordinal);
        Assert.True(a >= 16 && b > a);
        return text[a..b];
    }

    public static TheoryData<string> DirtyWriters() => new()
    {
        "DirtySql", "MarkDirtySql", "DeleteFollowSql", "OpenRequestsSql", "DeletionFollowsSql",
    };

    private static string Writer(string name) => name switch
    {
        "DirtySql" => FollowEffectsWork.DirtySql,
        "MarkDirtySql" => SocialSql.MarkDirtySql,
        "DeleteFollowSql" => SocialSql.DeleteFollowSql,
        "OpenRequestsSql" => SocialProfileService.OpenRequestsSql,
        _ => DeletionFollowsSql(),
    };

    [Theory]
    [MemberData(nameof(DirtyWriters))]
    public void Every_dirty_write_takes_the_row_lock_in_id_order(string name)
    {
        var sql = Writer(name);
        Assert.Contains("INSERT INTO \"FollowCountDirty\"", sql);
        Assert.Contains(Upsert, sql);
        Assert.DoesNotContain("DO NOTHING", sql);
        Assert.Matches(new Regex(@"ORDER BY \w+\.id\s+ON CONFLICT"), sql);
    }

    [Fact]
    public void Recount_deletes_with_skip_locked_then_counts_in_its_own_statement()
    {
        var pick = FollowEffectsWork.PickDirtySql;
        Assert.StartsWith("DELETE FROM \"FollowCountDirty\"", pick.TrimStart());
        Assert.Contains("ORDER BY \"UserId\" LIMIT @batch FOR UPDATE SKIP LOCKED", pick);
        Assert.Contains("RETURNING \"UserId\" AS \"Value\"", pick);
        Assert.DoesNotContain("UserProfiles", pick);

        var count = FollowEffectsWork.RecountSql;
        Assert.StartsWith("UPDATE \"UserProfiles\"", count.TrimStart());
        Assert.Contains("= ANY(@ids)", count);
        Assert.DoesNotContain("FollowCountDirty", count);
        Assert.DoesNotContain("WITH", count);
        Assert.Contains($"f.\"Status\" = '{FollowStatuses.Accepted}'", count);

        var job = Src("RentNearBy.Infrastructure", "Services", "Social", "FollowEffectsJob.cs");
        var a = job.IndexOf("public async Task<int> RecountAsync", StringComparison.Ordinal);
        var body = job[a..job.IndexOf("// Step 3", a, StringComparison.Ordinal)];
        Assert.Contains("BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted", body);
        Assert.True(body.IndexOf("PickDirtySql", StringComparison.Ordinal) < body.IndexOf("RecountSql", StringComparison.Ordinal));
        Assert.Contains("tx.CommitAsync", body);
    }

    [Fact]
    public void Claim_opens_push_groups_in_receiver_kind_order()
        => Assert.Matches(new Regex(@"ORDER BY o\.rid, o\.kind\s+ON CONFLICT \(""ReceiverId"", ""Kind""\)"), FollowEffectsWork.OpenGroupsSql);

    [Fact]
    public void Claim_writes_dirty_marks_before_bell_rows_like_the_user_path()
    {
        var job = Src("RentNearBy.Infrastructure", "Services", "Social", "FollowEffectsJob.cs");
        var a = job.IndexOf("public async Task<int> ClaimAsync", StringComparison.Ordinal);
        var dirty = job.IndexOf("ExecuteSqlRawAsync(DirtySql", a, StringComparison.Ordinal);
        var bell = job.IndexOf("notices.UpsertAsync(NoticeRows(batch)", a, StringComparison.Ordinal);
        var groups = job.IndexOf("ExecuteSqlRawAsync(OpenGroupsSql", a, StringComparison.Ordinal);
        Assert.True(dirty > a && bell > dirty && groups > bell);
    }

    [Fact]
    public void Bell_rows_are_sorted_by_notice_key()
    {
        Guid G(int n) => new($"00000000-0000-0000-0000-{n:D12}");
        var t = new DateTime(2026, 11, 28, 6, 0, 0, DateTimeKind.Utc);
        var batch = new[]
        {
            new FollowEffectsWork.PendingFollow { FollowerId = G(5), FolloweeId = G(2), Status = FollowStatuses.Accepted, CreatedAt = t },
            new FollowEffectsWork.PendingFollow { FollowerId = G(1), FolloweeId = G(9), Status = FollowStatuses.Requested, CreatedAt = t },
            new FollowEffectsWork.PendingFollow { FollowerId = G(3), FolloweeId = G(2), Status = FollowStatuses.Accepted, CreatedAt = t },
            new FollowEffectsWork.PendingFollow { FollowerId = G(4), FolloweeId = G(1), Status = FollowStatuses.Accepted, CreatedAt = t },
        };
        var rows = FollowEffectsWork.NoticeRows(batch);
        Assert.Equal(new[] { G(1), G(2), G(2), G(9) }, rows.Select(r => r.UserId).ToArray());
        Assert.Equal(new[] { G(4), G(3), G(5), G(1) }, rows.Select(r => r.TripId).ToArray());
        Assert.Equal(NoticeKinds.FollowRequest, rows[3].Kind);
        Assert.Equal(rows.Select(r => r.TripId).ToArray(), rows.Select(r => r.ActorUserId!.Value).ToArray());
    }

    // .NET Guid.CompareTo reads the first field as a little-endian int; Postgres compares the uuid bytes as written.
    [Fact]
    public void Bell_rows_follow_postgres_uuid_order_not_dotnet_guid_order()
    {
        var low = new Guid("01000000-0000-0000-0000-000000000000");
        var high = new Guid("00000001-0000-0000-0000-000000000000");
        Assert.True(low.CompareTo(high) > 0);
        var t = new DateTime(2026, 11, 28, 6, 0, 0, DateTimeKind.Utc);
        var rows = FollowEffectsWork.NoticeRows(
        [
            new FollowEffectsWork.PendingFollow { FollowerId = high, FolloweeId = high, Status = FollowStatuses.Accepted, CreatedAt = t },
            new FollowEffectsWork.PendingFollow { FollowerId = low, FolloweeId = low, Status = FollowStatuses.Accepted, CreatedAt = t },
        ]);
        Assert.Equal(new[] { high, low }, rows.Select(r => r.UserId).ToArray());
    }
}
