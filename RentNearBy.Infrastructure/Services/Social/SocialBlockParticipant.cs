using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Interfaces.Buddy;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Data;
using static RentNearBy.Infrastructure.Services.Social.SocialSql;

namespace RentNearBy.Infrastructure.Services.Social;

// Contract 1.10, inside the block transaction (after the pair lock): both follow rows go (counters and buddy rows with them),
// waiting join requests between the two close, their direct room is blocked and hidden for both.
public sealed class SocialBlockParticipant(ApplicationDbContext db) : IUserBlockParticipant
{
    private static readonly string FollowNoticesSql = $"""
        DELETE FROM "UserNotices"
        WHERE "Kind" IN ('{NoticeKinds.FollowRequest}', '{NoticeKinds.NewFollower}', '{NoticeKinds.FollowAccepted}')
          AND (("UserId" = @a AND "TripId" = @b) OR ("UserId" = @b AND "TripId" = @a))
        """;

    private static readonly string CloseJoinSql = $"""
        WITH r AS (
          UPDATE "TripJoinRequests" SET "Status" = '{JoinRequestStatuses.ClosedByBlock}', "EndedAt" = now()
          WHERE "Status" = '{JoinRequestStatuses.Waiting}'
            AND (("RequesterId" = @a AND "OwnerUserId" = @b) OR ("RequesterId" = @b AND "OwnerUserId" = @a))
          RETURNING "Id")
        DELETE FROM "UserNotices" WHERE "Kind" = '{NoticeKinds.JoinRequest}' AND "TripId" IN (SELECT "Id" FROM r)
        """;

    private static readonly string BlockRoomSql = $"""
        WITH r AS (
          UPDATE "ChatRooms" SET "BlockedAt" = now()
          WHERE "Kind" = '{ChatKinds.Direct}' AND "PairLowId" = LEAST(@a, @b) AND "PairHighId" = GREATEST(@a, @b)
          RETURNING "Id")
        UPDATE "ChatRoomMembers" SET "ListBucket" = '{ChatBuckets.Hidden}', "UnreadCount" = 0 WHERE "RoomId" IN (SELECT "Id" FROM r)
        """;

    // Unblock (UserBlockService): the room comes back only when no block row is left either way.
    public static readonly string UnblockRoomSql = $"""
        WITH r AS (
          UPDATE "ChatRooms" SET "BlockedAt" = NULL
          WHERE "Kind" = '{ChatKinds.Direct}' AND "PairLowId" = LEAST(@me, @him) AND "PairHighId" = GREATEST(@me, @him)
            AND "BlockedAt" IS NOT NULL AND NOT {BlockedEitherWay("@me", "@him")}
          RETURNING "Id")
        UPDATE "ChatRoomMembers" SET "ListBucket" = '{ChatBuckets.People}'
        WHERE "RoomId" IN (SELECT "Id" FROM r) AND "ListBucket" = '{ChatBuckets.Hidden}' AND "LeftAt" IS NULL
        """;

    public async Task OnBlockedAsync(Guid blockerId, Guid blockedId, CancellationToken ct = default)
    {
        await DeleteFollowAsync(db, blockerId, blockedId, ct);
        await DeleteFollowAsync(db, blockedId, blockerId, ct);
        await db.Database.ExecuteSqlRawAsync(FollowNoticesSql, [Uuid("a", blockerId), Uuid("b", blockedId)], ct);
        await db.Database.ExecuteSqlRawAsync(CloseJoinSql, [Uuid("a", blockerId), Uuid("b", blockedId)], ct);
        await db.Database.ExecuteSqlRawAsync(BlockRoomSql, [Uuid("a", blockerId), Uuid("b", blockedId)], ct);
    }
}
