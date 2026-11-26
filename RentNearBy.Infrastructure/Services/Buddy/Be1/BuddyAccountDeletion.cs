using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Interfaces.Buddy;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services.Buddy.Be1;

// Contract 3.3 deletion matrix, Order 20: notices left only by his requests go, his blocks go both ways.
// BuddyRequests and Buddies go by FK cascade with the Users row.
public sealed class BuddyAccountDeletion(ApplicationDbContext db) : IAccountDeletionParticipant
{
    private static readonly string DeleteNoticesSql = $"""
        DELETE FROM "UserNotices" n
        WHERE n."Kind" = '{NoticeKinds.BuddyRequest}' AND n."TripId" = '{NoticeKinds.NoTrip}'
          AND n."UserId" IN (SELECT r."ToUserId" FROM "BuddyRequests" r WHERE r."FromUserId" = @me AND r."Status" = '{BuddyRequestStatuses.Pending}')
          AND NOT EXISTS (SELECT 1 FROM "BuddyRequests" o
                          WHERE o."ToUserId" = n."UserId" AND o."FromUserId" <> @me
                            AND o."Status" = '{BuddyRequestStatuses.Pending}' AND o."ExpiresAt" > now())
        """;

    private const string DeleteBlocksSql = """DELETE FROM "UserBlocks" WHERE "BlockerId" = @me OR "BlockedId" = @me""";

    // Everyone he shares a pair lock with: buddies (B10, block, S2 invites) and open requests either way (accept, decline, cancel).
    internal static readonly string PartnersSql = $"""
        SELECT b."BuddyUserId" AS "Value" FROM "Buddies" b WHERE b."UserId" = @me
        UNION SELECT b."UserId" FROM "Buddies" b WHERE b."BuddyUserId" = @me
        UNION SELECT r."ToUserId" FROM "BuddyRequests" r WHERE r."FromUserId" = @me AND r."Status" = '{BuddyRequestStatuses.Pending}'
        UNION SELECT r."FromUserId" FROM "BuddyRequests" r WHERE r."ToUserId" = @me AND r."Status" = '{BuddyRequestStatuses.Pending}'
        """;

    public int Order => 20;

    // Read without locks, then every pair lock in one sorted statement (BuddyLocks.LockPairsAsync), before the Users row lock.
    public async Task LockAsync(Guid userId, CancellationToken ct = default)
    {
        var partners = await db.Database.SqlQueryRaw<Guid>(PartnersSql, Be1Sql.Uuid("me", userId)).ToListAsync(ct);
        if (partners.Count > 0) await BuddyLocks.LockPairsAsync(db, userId, partners, ct);
    }

    public Task<IReadOnlyList<AccountDeletionBlock>> CheckAsync(Guid userId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AccountDeletionBlock>>([]);

    public async Task OnDeletingAsync(Guid userId, CancellationToken ct = default)
    {
        await db.Database.ExecuteSqlRawAsync(DeleteNoticesSql, [Be1Sql.Uuid("me", userId)], ct);
        await db.Database.ExecuteSqlRawAsync(DeleteBlocksSql, [Be1Sql.Uuid("me", userId)], ct);
    }

    public Task AfterCommitAsync(Guid userId, CancellationToken ct = default) => Task.CompletedTask;
}
