using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Interfaces.Buddy;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services.Buddy.Be1;

// Hourly: overdue Pending requests -> Expired, then the BuddyRequest notices of receivers with nothing left. One statement per batch.
public sealed class BuddyRequestExpiryJob(ApplicationDbContext db) : IBuddyFeatureJob
{
    internal static readonly string ExpireBatchSql = $"""
        WITH expired AS (
          UPDATE "BuddyRequests" SET "Status" = '{BuddyRequestStatuses.Expired}', "EndedAt" = now()
          WHERE "Id" IN (SELECT "Id" FROM "BuddyRequests"
                         WHERE "Status" = '{BuddyRequestStatuses.Pending}' AND "ExpiresAt" <= now()
                         ORDER BY "ExpiresAt" LIMIT @batch FOR UPDATE SKIP LOCKED)
          RETURNING "ToUserId"
        ), gone AS (
          DELETE FROM "UserNotices" n
          WHERE n."Kind" = '{NoticeKinds.BuddyRequest}' AND n."TripId" = '{NoticeKinds.NoTrip}'
            AND n."UserId" IN (SELECT DISTINCT e."ToUserId" FROM expired e)
            AND NOT EXISTS (SELECT 1 FROM "BuddyRequests" r
                            WHERE r."ToUserId" = n."UserId" AND r."Status" = '{BuddyRequestStatuses.Pending}' AND r."ExpiresAt" > now())
        )
        SELECT count(*)::int AS "Value" FROM expired
        """;

    public string Name => "buddy-request-expiry";

    public TimeSpan Interval => TimeSpan.FromHours(1);

    public TimeOnly? DailyAtIst => null;

    public async Task<int> RunAsync(CancellationToken ct)
    {
        var total = 0;
        int batch;
        do // batch-loop
        {
            batch = (await db.Database.SqlQueryRaw<int>(ExpireBatchSql, Be1Sql.Int("batch", BuddyLimits.JobBatchSize)).ToListAsync(ct))[0]; // batch-loop
            total += batch;
        } while (batch == BuddyLimits.JobBatchSize && !ct.IsCancellationRequested);
        return total;
    }
}
