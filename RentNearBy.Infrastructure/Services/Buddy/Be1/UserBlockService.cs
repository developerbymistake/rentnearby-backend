using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Interfaces.Buddy;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.TaxiRequests;

namespace RentNearBy.Infrastructure.Services.Buddy.Be1;

// The one block of the app. Chat routes and B12/B13 call it. One transaction under the pair lock; nobody is told.
public sealed class UserBlockService(
    ApplicationDbContext db, IEnumerable<IUserBlockParticipant> participants, ITripEnquiryClosure tripClosure,
    IBuddyLiveHints hints, ILogger<UserBlockService> logger) : IUserBlockService
{
    private const string BlockStateSql = """
        SELECT EXISTS (SELECT 1 FROM "Users" u WHERE u."Id" = @him) AS "TargetExists",
               EXISTS (SELECT 1 FROM "UserBlocks" b WHERE b."BlockerId" = @me AND b."BlockedId" = @him) AS "Already",
               (SELECT count(*) FROM "UserBlocks" b WHERE b."BlockerId" = @me)::int AS "Count"
        """;

    private const string InsertBlockSql = """
        INSERT INTO "UserBlocks" ("Id", "BlockerId", "BlockedId", "CreatedAt") VALUES (gen_random_uuid(), @me, @him, now())
        ON CONFLICT DO NOTHING
        """;

    private static readonly string CloseRequestsSql = $"""
        UPDATE "BuddyRequests" SET "Status" = '{BuddyRequestStatuses.ClosedByBlock}', "BlockedSender" = ("FromUserId" = @him), "EndedAt" = now()
        WHERE "PairLowId" = LEAST(@me, @him) AND "PairHighId" = GREATEST(@me, @him) AND "Status" = '{BuddyRequestStatuses.Pending}'
        """;

    private const string DeleteBuddiesSql = """
        DELETE FROM "Buddies" WHERE ("UserId" = @me AND "BuddyUserId" = @him) OR ("UserId" = @him AND "BuddyUserId" = @me)
        """;

    private const string BlockConversationsSql = """
        UPDATE "Conversations" SET "Status" = 'Blocked'
        WHERE (("RenterId" = @me AND "OwnerId" = @him) OR ("RenterId" = @him AND "OwnerId" = @me)) AND "Status" <> 'Blocked'
        RETURNING "Id" AS "Value"
        """;

    private const string DeleteBlockSql = """DELETE FROM "UserBlocks" WHERE "BlockerId" = @me AND "BlockedId" = @him""";

    // A conversation reopens only when the other side has not blocked the caller too.
    private const string UnblockConversationsSql = """
        UPDATE "Conversations" SET "Status" = 'Active'
        WHERE (("RenterId" = @me AND "OwnerId" = @him) OR ("RenterId" = @him AND "OwnerId" = @me)) AND "Status" = 'Blocked'
          AND NOT EXISTS (SELECT 1 FROM "UserBlocks" b WHERE b."BlockerId" = @him AND b."BlockedId" = @me)
        RETURNING "Id" AS "Value"
        """;

    public async Task<BlockOutcome> BlockAsync(Guid blockerId, Guid blockedId, CancellationToken ct = default)
    {
        if (blockerId == blockedId) return BlockOutcome.Missing;
        var outcome = await PgRetry.OnceAsync(db, async c =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(c);
            await BuddyLocks.LockPairAsync(db, blockerId, blockedId, c);
            var s = (await db.Database.SqlQueryRaw<BlockState>(BlockStateSql, Me(blockerId), Him(blockedId)).ToListAsync(c))[0];
            if (!s.TargetExists) return BlockOutcome.Missing;
            if (!s.Already && s.Count >= BuddyLimits.MaxBlocks) return BlockOutcome.Full;

            await db.Database.ExecuteSqlRawAsync(InsertBlockSql, [Me(blockerId), Him(blockedId)], c);
            await db.Database.ExecuteSqlRawAsync(CloseRequestsSql, [Me(blockerId), Him(blockedId)], c);
            await db.Database.ExecuteSqlRawAsync(DeleteBuddiesSql, [Me(blockerId), Him(blockedId)], c);
            await Be1Sql.DeleteRequestNoticesAsync(db, [blockerId, blockedId], c);
            foreach (var participant in participants) // batch-loop: one call per registered participant (BE2 invites), never per row
                await participant.OnBlockedAsync(blockerId, blockedId, c);
            var flipped = await db.Database.SqlQueryRaw<Guid>(BlockConversationsSql, Me(blockerId), Him(blockedId)).ToListAsync(c);
            await tx.CommitAsync(c);
            return new BlockOutcome(BlockResult.Done, flipped);
        }, ct, logger);

        if (outcome.Result != BlockResult.Done) return outcome;
        try
        {
            await tripClosure.OnBlockAsync(blockerId, blockedId);
            await db.SaveChangesAsync(ct);
            await tripClosure.AfterCommitAsync([]);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "trip.block.closure_failed");
        }
        // Nobody is told: the blocked side picks the change up on its next normal load.
        await hints.BuddiesChangedAsync([blockerId]);
        return outcome;
    }

    public async Task<BlockOutcome> UnblockAsync(Guid blockerId, Guid blockedId, CancellationToken ct = default)
    {
        if (blockerId == blockedId) return BlockOutcome.Missing;
        return await PgRetry.OnceAsync(db, async c =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(c);
            await BuddyLocks.LockPairAsync(db, blockerId, blockedId, c);
            if (await db.Database.ExecuteSqlRawAsync(DeleteBlockSql, [Me(blockerId), Him(blockedId)], c) == 0)
            {
                await tx.CommitAsync(c);
                return new BlockOutcome(BlockResult.Done, []);
            }
            var flipped = await db.Database.SqlQueryRaw<Guid>(UnblockConversationsSql, Me(blockerId), Him(blockedId)).ToListAsync(c);
            await tx.CommitAsync(c);
            return new BlockOutcome(BlockResult.Done, flipped);
        }, ct, logger);
    }

    private static Npgsql.NpgsqlParameter Me(Guid id) => Be1Sql.Uuid("me", id);

    private static Npgsql.NpgsqlParameter Him(Guid id) => Be1Sql.Uuid("him", id);

    private sealed class BlockState
    {
        public bool TargetExists { get; set; }
        public bool Already { get; set; }
        public int Count { get; set; }
    }
}
