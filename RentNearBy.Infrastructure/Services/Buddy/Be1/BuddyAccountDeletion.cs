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

    public int Order => 20;

    public Task<IReadOnlyList<AccountDeletionBlock>> CheckAsync(Guid userId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AccountDeletionBlock>>([]);

    public async Task OnDeletingAsync(Guid userId, CancellationToken ct = default)
    {
        await db.Database.ExecuteSqlRawAsync(DeleteNoticesSql, [Be1Sql.Uuid("me", userId)], ct);
        await db.Database.ExecuteSqlRawAsync(DeleteBlocksSql, [Be1Sql.Uuid("me", userId)], ct);
    }

    public Task AfterCommitAsync(Guid userId, CancellationToken ct = default) => Task.CompletedTask;
}
