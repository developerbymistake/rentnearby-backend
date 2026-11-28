using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Interfaces.Buddy;
using RentNearBy.Core.Interfaces.Social;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Data;
using static RentNearBy.Infrastructure.Services.Social.SocialSql;

namespace RentNearBy.Infrastructure.Services.Social;

// Contract 1.10: account deletion. The other side of every deleted follow is marked for the FollowEffectsJob recount, waiting join requests end
// Cancelled (owners' lists refresh), direct rooms go, snapshots keep the messages with the name replaced. Profiles, audit,
// buddy rows, trip-room memberships and the rest go by the Users cascade.
public sealed class SocialDeletionParticipant(ApplicationDbContext db, IHubSessionRevoker? hubSessions = null) : IAccountDeletionParticipant
{
    public int Order => 25;

    private const string FollowsSql = """
        WITH d AS (DELETE FROM "Follows" WHERE "FollowerId" = @me OR "FolloweeId" = @me RETURNING "FollowerId", "FolloweeId"),
        dirty AS (INSERT INTO "FollowCountDirty" ("UserId", "MarkedAt")
                  SELECT o.id, now()
                  FROM (SELECT DISTINCT CASE WHEN d."FollowerId" = @me THEN d."FolloweeId" ELSE d."FollowerId" END AS id FROM d) o
                  ORDER BY o.id
                  ON CONFLICT ("UserId") DO UPDATE SET "MarkedAt" = EXCLUDED."MarkedAt")
        SELECT count(*)::int AS "Value" FROM d
        """;

    private static readonly string JoinSql = $"""
        WITH r AS (
          UPDATE "TripJoinRequests" SET "Status" = '{JoinRequestStatuses.Cancelled}', "EndedAt" = now()
          WHERE "RequesterId" = @me AND "Status" = '{JoinRequestStatuses.Waiting}'
          RETURNING "Id")
        DELETE FROM "UserNotices" WHERE "Kind" = '{NoticeKinds.JoinRequest}' AND "TripId" IN (SELECT "Id" FROM r)
        """;

    private static readonly string RoomsSql = $"""
        DELETE FROM "ChatRooms" WHERE "Kind" = '{ChatKinds.Direct}' AND ("PairLowId" = @me OR "PairHighId" = @me)
        """;

    private const string SnapshotsSql = """
        UPDATE "MessageSnapshots" s
        SET "MessagesJson" = (SELECT COALESCE(jsonb_agg(CASE WHEN e->>'senderId' = @me::text
                                                             THEN jsonb_set(e, '{{senderName}}','"Deleted account"'::jsonb)
                                                             ELSE e END ORDER BY ord), '[]'::jsonb)
                              FROM jsonb_array_elements(s."MessagesJson") WITH ORDINALITY AS x(e, ord))
        WHERE s."MessagesJson" @> jsonb_build_array(jsonb_build_object('senderId', @me::text))
        """;

    private const string SocialNoticesSql = """
        DELETE FROM "UserNotices" WHERE "TripId" = @me AND "Kind" IN ('FollowRequest', 'NewFollower', 'FollowAccepted')
        """;

    public Task<IReadOnlyList<AccountDeletionBlock>> CheckAsync(Guid userId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AccountDeletionBlock>>([]);

    public async Task OnDeletingAsync(Guid userId, CancellationToken ct = default)
    {
        var me = Uuid("me", userId);
        await db.Database.SqlQueryRaw<int>(FollowsSql, me).ToListAsync(ct);
        await db.Database.ExecuteSqlRawAsync(JoinSql, [Uuid("me", userId)], ct);
        await db.Database.ExecuteSqlRawAsync(RoomsSql, [Uuid("me", userId)], ct);
        await db.Database.ExecuteSqlRawAsync(SnapshotsSql, [Uuid("me", userId)], ct);
        await db.Database.ExecuteSqlRawAsync(SocialNoticesSql, [Uuid("me", userId)], ct);
    }

    // His live chat sockets end after the deletion commits (contract 3.1).
    public Task AfterCommitAsync(Guid userId, CancellationToken ct = default)
        => hubSessions is null ? Task.CompletedTask : hubSessions.RevokeAsync(userId, "accountDeleted");
}
