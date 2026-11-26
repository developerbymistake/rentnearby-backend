using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.DTOs.Social;
using RentNearBy.Core.Interfaces.Social;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Data;
using static RentNearBy.Infrastructure.Services.Social.SocialSql;

namespace RentNearBy.Infrastructure.Services.Social;

// Contract 3.5: the trip group chat. Runs inside the caller's transaction (join accept, invite accept, remove, leave);
// events go to SocialAfterCommit, flushed by the caller after its commit.
public sealed class TripGroupChat(ApplicationDbContext db, SocialAfterCommit afterCommit) : ITripGroupChat
{
    private static readonly string EnsureRoomSql = $"""
        INSERT INTO "ChatRooms" ("Id", "Kind", "TripId", "Status", "LastSeq", "CreatedAt")
        VALUES (gen_random_uuid(), '{ChatKinds.Trip}', @trip, '{ChatRoomStatuses.Active}', 0, now())
        ON CONFLICT ("TripId") WHERE "Kind" = '{ChatKinds.Trip}' DO NOTHING
        """;

    private static readonly string LockRoomSql = $"""
        SELECT r."Id" AS "RoomId", r."LastSeq" FROM "ChatRooms" r WHERE r."TripId" = @trip AND r."Kind" = '{ChatKinds.Trip}' FOR UPDATE
        """;

    // Owner + every Joined member; a member who left before and joined again comes back from the current seq.
    private static readonly string MembersSql = $"""
        INSERT INTO "ChatRoomMembers" ("RoomId", "UserId", "LastReadSeq", "UnreadCount", "ListBucket", "ClearedUpToSeq", "JoinedSeq", "SortAt")
        SELECT @room, x.uid, @seq, 0, '{ChatBuckets.Trips}', 0, @seq, now()
        FROM (SELECT t."OwnerUserId" AS uid FROM "Trips" t WHERE t."Id" = @trip
              UNION SELECT m."UserId" FROM "TripMembers" m WHERE m."TripId" = @trip AND m."Status" = '{TripMemberStatuses.Joined}') x
        ON CONFLICT ("RoomId", "UserId") DO UPDATE SET "LeftAt" = NULL, "ListBucket" = '{ChatBuckets.Trips}', "UnreadCount" = 0,
            "JoinedSeq" = EXCLUDED."JoinedSeq", "LastReadSeq" = EXCLUDED."LastReadSeq", "ClearedUpToSeq" = 0, "SortAt" = now()
        WHERE "ChatRoomMembers"."LeftAt" IS NOT NULL
        """;

    private const string NameSql = """SELECT COALESCE(u."Name", '') AS "Value" FROM "Users" u WHERE u."Id" = @user""";

    private static readonly string SystemLineSql = $"""
        WITH b AS (
          UPDATE "ChatRooms" SET "LastSeq" = "LastSeq" + 1, "LastMessageAt" = now(), "LastPreview" = @body
          WHERE "Id" = @room RETURNING "LastSeq"),
        i AS (
          INSERT INTO "ChatMessages" ("RoomId", "Seq", "SenderId", "SystemKind", "ActorUserId", "Body", "Masked", "CreatedAt", "ExpiresAt")
          SELECT @room, b."LastSeq", NULL, @kind, @user, @body, FALSE, now(), now() + {Interval(SocialLimits.MessageLife)} FROM b
          RETURNING "Seq", "CreatedAt", "ExpiresAt")
        SELECT i."Seq", i."CreatedAt", i."ExpiresAt" FROM i
        """;

    private const string FanOutSql = """
        UPDATE "ChatRoomMembers" m SET "SortAt" = now(),
               "UnreadCount" = m."UnreadCount" + CASE WHEN m."UserId" <> @user THEN 1 ELSE 0 END
        WHERE m."RoomId" = @room AND m."LeftAt" IS NULL
        RETURNING m."UserId" AS "Value"
        """;

    private static readonly string LeaveSql = $"""
        UPDATE "ChatRoomMembers" m SET "LeftAt" = now(), "ListBucket" = '{ChatBuckets.Hidden}', "UnreadCount" = 0
        FROM "ChatRooms" r
        WHERE r."Id" = m."RoomId" AND r."TripId" = @trip AND r."Kind" = '{ChatKinds.Trip}' AND m."UserId" = @user AND m."LeftAt" IS NULL
        RETURNING m."RoomId" AS "Value"
        """;

    public async Task<Guid> OnMemberJoinedAsync(Guid tripId, Guid userId, CancellationToken ct = default)
    {
        await db.Database.ExecuteSqlRawAsync(EnsureRoomSql, [Uuid("trip", tripId)], ct);
        var room = (await db.Database.SqlQueryRaw<RoomRef>(LockRoomSql, Uuid("trip", tripId)).ToListAsync(ct)).First();
        await db.Database.ExecuteSqlRawAsync(MembersSql, [Uuid("room", room.RoomId), Uuid("trip", tripId), Long("seq", room.LastSeq)], ct);
        var name = (await db.Database.SqlQueryRaw<string>(NameSql, Uuid("user", userId)).ToListAsync(ct)).FirstOrDefault();
        await SystemLineAsync(room.RoomId, userId, ChatSystemKinds.Joined, $"{SocialRules.FirstName(name)} joined the trip", ct);
        return room.RoomId;
    }

    public async Task OnMemberGoneAsync(Guid tripId, Guid userId, CancellationToken ct = default)
    {
        var rooms = await db.Database.SqlQueryRaw<Guid>(LeaveSql, Uuid("trip", tripId), Uuid("user", userId)).ToListAsync(ct);
        if (rooms.Count == 0) return;
        var name = (await db.Database.SqlQueryRaw<string>(NameSql, Uuid("user", userId)).ToListAsync(ct)).FirstOrDefault();
        await SystemLineAsync(rooms[0], userId, ChatSystemKinds.Left, $"{SocialRules.FirstName(name)} left the trip", ct);
        afterCommit.Add(new SocialHubEvent(userId, SocialHubEventNames.DmRoomChanged,
            new { roomId = rooms[0], status = ChatRoomStatuses.Active, @lock = ChatLocks.Left }));
        afterCommit.UnreadFor([userId]);
    }

    private async Task SystemLineAsync(Guid roomId, Guid userId, string kind, string body, CancellationToken ct)
    {
        var line = (await db.Database.SqlQueryRaw<LineRow>(SystemLineSql,
            Uuid("room", roomId), Uuid("user", userId), Text("kind", kind), Text("body", body)).ToListAsync(ct)).First();
        var members = await db.Database.SqlQueryRaw<Guid>(FanOutSql, Uuid("room", roomId), Uuid("user", userId)).ToListAsync(ct);
        var message = new ChatMessageDto
        {
            Seq = line.Seq, Text = body, System = kind, CreatedAt = Utc(line.CreatedAt), ExpiresAt = Utc(line.ExpiresAt),
        };
        afterCommit.AddRange(members.Select(u => new SocialHubEvent(u, SocialHubEventNames.DmMessage,
            new { roomId, kind = ChatKinds.Trip, roomStatus = ChatRoomStatuses.Active, message })));
        afterCommit.UnreadFor(members.Where(u => u != userId));
    }

    private sealed class RoomRef
    {
        public Guid RoomId { get; set; }
        public long LastSeq { get; set; }
    }

    private sealed class LineRow
    {
        public long Seq { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime ExpiresAt { get; set; }
    }
}
