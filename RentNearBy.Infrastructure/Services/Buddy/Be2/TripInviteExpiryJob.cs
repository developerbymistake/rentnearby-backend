using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Interfaces.Buddy;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Infrastructure.Data;
using static RentNearBy.Infrastructure.Services.Buddy.Be2.Be2Sql;

namespace RentNearBy.Infrastructure.Services.Buddy.Be2;

// Hourly (contract 6): overdue invites -> Expired in batches of 1000, then ONE delete of their TripInvite notices per batch.
// S7/S8 already treat an overdue invite as expired at read time; this job only makes it permanent.
public sealed class TripInviteExpiryJob(ApplicationDbContext db) : IBuddyFeatureJob
{
    private static readonly string ExpireSql = $"""
        UPDATE "TripMembers" SET "Status" = 'Expired', "EndedAt" = now(), "UpdatedAt" = now(), "Version" = "Version" + 1
        WHERE "Id" IN (SELECT "Id" FROM "TripMembers"
                       WHERE "Status" = 'Invited' AND "InviteExpiresAt" <= now()
                       ORDER BY "InviteExpiresAt"
                       LIMIT {BuddyLimits.JobBatchSize}
                       FOR UPDATE SKIP LOCKED)
        RETURNING "UserId", "TripId"
        """;

    public string Name => "TripInviteExpiry";

    public TimeSpan Interval => TimeSpan.FromHours(1);

    public TimeOnly? DailyAtIst => null;

    public async Task<int> RunAsync(CancellationToken ct)
    {
        var total = 0;
        while (!ct.IsCancellationRequested) // batch-loop
        {
            var batch = await ExpireBatchAsync(ct); // batch-loop
            total += batch;
            if (batch < BuddyLimits.JobBatchSize) break;
        }
        return total;
    }

    private async Task<int> ExpireBatchAsync(CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var rows = await db.Database.SqlQueryRaw<PairRow>(ExpireSql).ToListAsync(ct);
        await DeleteInviteNoticesAsync(db, rows.Select(r => (r.UserId, r.TripId)).ToList(), ct);
        await tx.CommitAsync(ct);
        return rows.Count;
    }

    private sealed class PairRow
    {
        public Guid UserId { get; set; }
        public Guid TripId { get; set; }
    }
}
