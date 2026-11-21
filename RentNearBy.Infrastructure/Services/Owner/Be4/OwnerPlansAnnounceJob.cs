using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Interfaces.Buddy;
using RentNearBy.Core.Interfaces.Owner;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.TaxiRequests;
using static RentNearBy.Infrastructure.Services.Owner.Be4.OwnerJobSql;

namespace RentNearBy.Infrastructure.Services.Owner.Be4;

// AU-17 owner.plans.announce (every run of the 5-minute service, one cheap read when idle): while payment is OFF and the announce date
// is set and in the future, every owner with a submitted listing (not Draft, not deleted) and no active admin grant gets the stored
// PlansStarting notice and one push, once per announce date. Per kind and batch of owners (keyset by UserId), one transaction:
// (1) notice upsert (a row of another date is moved to the new date and made unread; the same date writes nothing) RETURNING,
// (2) ONE outbox insert for the returned owners with a valid token. The date rides in the row's Amount as yyyymmdd (a number, no text).
public sealed class OwnerPlansAnnounceJob(
    ApplicationDbContext db,
    IOwnerNotifier notifier,
    IPlanCatalogCache catalog,
    IOptions<OwnerFeatureOptions> options,
    TimeProvider clock,
    ILogger<OwnerPlansAnnounceJob> logger) : IBuddyFeatureJob
{
    public string Name => "owner.plans.announce";

    public TimeSpan Interval => TimeSpan.FromMinutes(5);

    public TimeOnly? DailyAtIst => null;

    internal const string FlagSql = """
        SELECT "IsEnabled", "AnnounceDate" FROM "AppFeatureFlags" WHERE "FeatureKey" = @key LIMIT 1
        """;

    internal static string BatchSql(string kind) => $"""
        WITH c AS (
            SELECT l."UserId"
            FROM {Table(kind)} l
            JOIN "Users" u ON u."Id" = l."UserId" AND u."IsActive"
            WHERE l."UserId" > @after AND NOT l."IsDeleted" AND l."Status" <> 'Draft'
              AND NOT EXISTS (SELECT 1 FROM "UserPlans" p
                              WHERE p."UserId" = l."UserId" AND p."FeatureKey" = '{kind}' AND p."Status" = '{UserPlanStatuses.Active}'
                                AND p."EndsAt" > now() AND p."Source" = '{UserPlanSources.AdminGrant}')
            ORDER BY l."UserId"
            LIMIT @n),
        ins AS (
            INSERT INTO "UserNotices" AS n ("Id", "UserId", "Kind", "TripId", "Amount", "Count", "CreatedAt", "UpdatedAt")
            SELECT gen_random_uuid(), c."UserId", '{NoticeKinds.PlansStarting}', @noTrip, @dateNumber, 1, now(), now() FROM c
            ON CONFLICT ("UserId", "Kind", "TripId") DO UPDATE SET
              "Amount" = EXCLUDED."Amount", "UpdatedAt" = now(), "ReadAt" = NULL, "Count" = 1
            WHERE n."Amount" IS DISTINCT FROM EXCLUDED."Amount"
            RETURNING n."UserId")
        SELECT c."UserId", (i."UserId" IS NOT NULL) AS "Written"
        FROM c LEFT JOIN ins i ON i."UserId" = c."UserId"
        ORDER BY c."UserId"
        """;

    public async Task<int> RunAsync(CancellationToken ct)
    {
        var flag = (await db.Database.SqlQueryRaw<FlagRow>(FlagSql, Text("key", AppFeatureKeys.PaymentEnabled)).ToListAsync(ct)).FirstOrDefault();
        var today = IstTime.TodayIst(clock.GetUtcNow().UtcDateTime);
        if (!ShouldAnnounce(flag?.IsEnabled ?? true, flag?.AnnounceDate, today)) return 0;
        var date = flag!.AnnounceDate!.Value;
        var snapshot = await catalog.GetAsync();
        var written = 0;
        foreach (var kind in new[] { ListingKinds.Stay, ListingKinds.Taxi }) // batch-loop
            written += await RunKindAsync(kind, date, snapshot, ct); // batch-loop
        if (written > 0) notifier.Wake();
        return written;
    }

    public static bool ShouldAnnounce(bool paymentEnabled, DateOnly? announceDate, DateOnly todayIst)
        => !paymentEnabled && announceDate is { } d && d > todayIst;

    public static int DateNumber(DateOnly d) => d.Year * 10000 + d.Month * 100 + d.Day;

    private async Task<int> RunKindAsync(string kind, DateOnly date, PlanCatalogSnapshot snapshot, CancellationToken ct)
    {
        var sql = BatchSql(kind);
        var batch = Math.Clamp(options.Value.JobBatch, 1, 5000);
        var after = Guid.Empty;
        var written = 0;
        while (true) // batch-loop
        {
            var cursor = after;
            var rows = await PgRetry.OnceAsync(db, async c =>
            {
                await using var tx = await db.Database.BeginTransactionAsync(c);
                var result = await db.Database.SqlQueryRaw<Row>(sql,
                    Uuid("after", cursor), Int("n", batch), Uuid("noTrip", NoticeKinds.NoTrip), Int("dateNumber", DateNumber(date))).ToListAsync(c);
                await notifier.PushAsync(Pushes(kind, date, snapshot, result.Where(r => r.Written).Select(r => r.UserId).ToList()), c);
                await tx.CommitAsync(c);
                return result;
            }, ct, logger);
            written += rows.Count(r => r.Written);
            if (rows.Count < batch) break;
            after = rows[^1].UserId;
        }
        return written;
    }

    // Pure: the push copy uses the Free plan values and the trial days of the kind (the trial sentence only while a trial exists).
    public static IReadOnlyList<OwnerPush> Pushes(string kind, DateOnly date, PlanCatalogSnapshot snapshot, IReadOnlyList<Guid> users)
    {
        var free = snapshot.DefaultValues(kind);
        var trialDays = snapshot.PlansOf(kind).Where(p => p.IsEnabled && !p.IsDefault).Select(p => p.TrialDays).DefaultIfEmpty(0).Max();
        return users.Select(u => kind == ListingKinds.Stay
            ? StayOwnerPushCopy.PlansStarting(u, date, free.Photos, free.RoomTypes, trialDays)
            : TaxiDriverPushCopy.PlansStarting(u, date, free.Routes, trialDays)).ToList();
    }

    private sealed class FlagRow
    {
        public bool IsEnabled { get; set; }
        public DateOnly? AnnounceDate { get; set; }
    }

    private sealed class Row
    {
        public Guid UserId { get; set; }
        public bool Written { get; set; }
    }
}
