using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Interfaces.Buddy;
using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services.Social;

// Contract 3.7: every 10 minutes, expired chat messages go in batches with the unread correction; then previews of rooms
// whose newest message expired are cleared and every affected user gets one DmUnread.
public sealed class ChatExpiryPurgeJob(ApplicationDbContext db, SocialAfterCommit afterCommit) : IBuddyFeatureJob
{
    public string Name => "social-chat-expiry";

    public TimeSpan Interval => TimeSpan.FromMinutes(10);

    public TimeOnly? DailyAtIst => null;

    internal static readonly string PurgeBatchSql = $"""
        WITH k AS (
          SELECT x."RoomId", x."Seq" FROM "ChatMessages" x WHERE x."ExpiresAt" <= now()
          ORDER BY x."ExpiresAt" LIMIT {SocialLimits.ChatPurgeBatch} FOR UPDATE SKIP LOCKED),
        d AS (
          DELETE FROM "ChatMessages" m USING k WHERE m."RoomId" = k."RoomId" AND m."Seq" = k."Seq"
          RETURNING m."RoomId", m."Seq", m."SenderId"),
        u AS (
          UPDATE "ChatRoomMembers" cm SET "UnreadCount" = GREATEST(0, cm."UnreadCount" - x.n)
          FROM (SELECT mm."RoomId", mm."UserId", count(*)::int AS n
                FROM d JOIN "ChatRoomMembers" mm ON mm."RoomId" = d."RoomId"
                WHERE mm."LeftAt" IS NULL AND d."Seq" > mm."LastReadSeq" AND d."Seq" > mm."JoinedSeq"
                  AND (d."SenderId" IS NULL OR d."SenderId" <> mm."UserId")
                GROUP BY mm."RoomId", mm."UserId") x
          WHERE cm."RoomId" = x."RoomId" AND cm."UserId" = x."UserId" AND x.n > 0
          RETURNING cm."UserId")
        SELECT (SELECT count(*) FROM d)::int AS "Deleted", ARRAY(SELECT DISTINCT u."UserId" FROM u) AS "Users"
        """;

    // Spec section 1: the preview goes only when the room's NEWEST message expired (a waiting first message of a Request room
    // lives 7 days). The 24 h bound is a cheap pre-filter (no message lives shorter); ix_chatrooms_preview serves it.
    internal static readonly string ClearPreviewSql = $"""
        UPDATE "ChatRooms" r SET "LastPreview" = NULL
        WHERE r."LastPreview" IS NOT NULL AND r."LastMessageAt" < now() - {SocialSql.Interval(SocialLimits.MessageLife)}
          AND NOT EXISTS (SELECT 1 FROM "ChatMessages" m WHERE m."RoomId" = r."Id" AND m."Seq" = r."LastSeq" AND m."ExpiresAt" > now())
        """;

    public async Task<int> RunAsync(CancellationToken ct)
    {
        afterCommit.Clear();
        var total = 0;
        var users = new HashSet<Guid>();
        for (var i = 0; i < SocialLimits.ChatPurgeBatches; i++)
        {
            var batch = (await db.Database.SqlQueryRaw<BatchRow>(PurgeBatchSql).ToListAsync(ct)).First(); // batch-loop
            total += batch.Deleted;
            foreach (var u in batch.Users ?? []) users.Add(u);
            if (batch.Deleted < SocialLimits.ChatPurgeBatch) break;
        }
        await db.Database.ExecuteSqlRawAsync(ClearPreviewSql, ct);
        if (users.Count > 0)
        {
            afterCommit.UnreadFor(users);
            await afterCommit.FlushAsync(ct);
        }
        return total;
    }

    private sealed class BatchRow
    {
        public int Deleted { get; set; }
        public Guid[]? Users { get; set; }
    }
}
