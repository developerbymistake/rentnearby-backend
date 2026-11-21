using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RentNearBy.Core.Interfaces.Buddy;
using RentNearBy.Core.Interfaces.Owner;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.TaxiRequests;
using static RentNearBy.Infrastructure.Services.Owner.Be4.OwnerJobSql;

namespace RentNearBy.Infrastructure.Services.Owner.Be4;

// Contract 5.1 owner.festival (10:00 IST): festivals starting in 19..21 days (stay owners) or 5..7 days (drivers); the 3-day window
// only covers a missed run, the mark table makes each (user, festival) once. Per festival, kind and batch of listings, one transaction:
// (1) mark insert ... RETURNING (state match or all India; stay: Approved or PendingReview and no special price touching the dates;
// taxi: Approved), (2) ONE UserNotices upsert for the new marks (always, even without a token), (3) ONE outbox insert (valid token only).
public sealed class FestivalReminderJob(
    ApplicationDbContext db,
    IOwnerNotifier notifier,
    IOptions<OwnerFeatureOptions> options,
    TimeProvider clock,
    ILogger<FestivalReminderJob> logger) : IBuddyFeatureJob
{
    public string Name => "owner.festival";

    public TimeSpan Interval => TimeSpan.FromDays(1);

    public TimeOnly? DailyAtIst => ParseIstTime(options.Value.FestivalJobIst) ?? new TimeOnly(10, 0);

    internal const string FestivalsSql = """
        SELECT f."Id", f."Name", f."DateFrom", f."DateTo", f."StateName"
        FROM "FestivalDates" f
        WHERE f."IsActive" AND ((f."DateFrom" BETWEEN @stayFrom AND @stayTo) OR (f."DateFrom" BETWEEN @taxiFrom AND @taxiTo))
        ORDER BY f."DateFrom", f."Id"
        """;

    internal static string MarkSql(string kind)
    {
        var statusRule = kind == ListingKinds.Stay ? "l.\"Status\" IN ('Approved', 'PendingReview')" : "l.\"Status\" = 'Approved'";
        var noRule = kind == ListingKinds.Stay
            ? """
              AND NOT EXISTS (SELECT 1 FROM "StaySpecialPrices" r
                              WHERE r."StayListingId" = l."Id" AND r."DateFrom" <= @to AND r."DateTo" >= @from)
              """
            : string.Empty;
        return $"""
            WITH c AS (
                SELECT l."Id", l."UserId"
                FROM {Table(kind)} l
                JOIN "Users" u ON u."Id" = l."UserId" AND u."IsActive"
                LEFT JOIN "Districts" d ON d."Id" = l."DistrictId"
                WHERE l."Id" > @after AND NOT l."IsDeleted" AND {statusRule}
                  AND (@state::text IS NULL OR d."StateName" = @state::text)
                  {noRule}
                ORDER BY l."Id"
                LIMIT @n),
            ins AS (
                INSERT INTO "FestivalReminderSent" ("UserId", "FestivalDateId", "FestivalDateTo", "SentAt")
                SELECT DISTINCT c."UserId", @festival, @to, now() FROM c
                ON CONFLICT ("UserId", "FestivalDateId") DO NOTHING
                RETURNING "UserId")
            SELECT c."Id" AS "ListingId", c."UserId", (i."UserId" IS NOT NULL) AS "Marked"
            FROM c LEFT JOIN ins i ON i."UserId" = c."UserId"
            ORDER BY c."Id"
            """;
    }

    public async Task<int> RunAsync(CancellationToken ct)
    {
        var today = IstTime.TodayIst(clock.GetUtcNow().UtcDateTime);
        var w = Windows(today);
        var festivals = await db.Database.SqlQueryRaw<FestivalRow>(FestivalsSql,
            Date("stayFrom", w.StayFrom), Date("stayTo", w.StayTo), Date("taxiFrom", w.TaxiFrom), Date("taxiTo", w.TaxiTo)).ToListAsync(ct);
        var written = 0;
        foreach (var f in festivals) // batch-loop
            foreach (var kind in KindsFor(f.DateFrom, w)) // batch-loop
                written += await RunFestivalAsync(f, kind, today, ct); // batch-loop
        if (written > 0) notifier.Wake();
        return written;
    }

    private async Task<int> RunFestivalAsync(FestivalRow f, string kind, DateOnly today, CancellationToken ct)
    {
        var sql = MarkSql(kind);
        var batch = Math.Clamp(options.Value.JobBatch, 1, 5000);
        var after = Guid.Empty;
        var written = 0;
        while (true) // batch-loop
        {
            var cursor = after;
            var rows = await PgRetry.OnceAsync(db, async c =>
            {
                await using var tx = await db.Database.BeginTransactionAsync(c);
                var marked = await db.Database.SqlQueryRaw<MarkRow>(sql,
                    Uuid("after", cursor), Int("n", batch), NullableText("state", f.StateName),
                    Uuid("festival", f.Id), Date("from", f.DateFrom), Date("to", f.DateTo)).ToListAsync(c);
                var (notices, pushes) = Plan(f, kind, today, marked.Where(m => m.Marked).Select(m => m.UserId).Distinct().ToList());
                await notifier.StoreAsync(notices, c);
                await notifier.PushAsync(pushes, c);
                await tx.CommitAsync(c);
                return marked;
            }, ct, logger);
            written += rows.Count(r => r.Marked);
            if (rows.Count < batch) break;
            after = rows[^1].ListingId;
        }
        return written;
    }

    public sealed record FestivalWindows(DateOnly StayFrom, DateOnly StayTo, DateOnly TaxiFrom, DateOnly TaxiTo);

    public static FestivalWindows Windows(DateOnly today)
    {
        var span = OwnerLimits.FestivalWindowDays - 1;
        return new(
            today.AddDays(OwnerLimits.FestivalStayLeadDays - span), today.AddDays(OwnerLimits.FestivalStayLeadDays),
            today.AddDays(OwnerLimits.FestivalTaxiLeadDays - span), today.AddDays(OwnerLimits.FestivalTaxiLeadDays));
    }

    public static IEnumerable<string> KindsFor(DateOnly festivalFrom, FestivalWindows w)
    {
        if (festivalFrom >= w.StayFrom && festivalFrom <= w.StayTo) yield return ListingKinds.Stay;
        if (festivalFrom >= w.TaxiFrom && festivalFrom <= w.TaxiTo) yield return ListingKinds.Taxi;
    }

    // Pure: one stored notice (ids only) and one push per newly marked owner.
    public static (IReadOnlyList<NoticeUpsert> Notices, IReadOnlyList<OwnerPush> Pushes) Plan(
        FestivalRow f, string kind, DateOnly today, IReadOnlyList<Guid> users)
    {
        var daysAway = f.DateFrom.DayNumber - today.DayNumber;
        var notices = users.Select(u => new NoticeUpsert(u, NoticeKinds.FestivalReminder, f.Id, null, f.Id)).ToList();
        var pushes = users.Select(u => kind == ListingKinds.Stay
            ? StayOwnerPushCopy.Festival(u, f.Id, f.Name, f.DateFrom, f.DateTo, daysAway)
            : TaxiDriverPushCopy.Festival(u, f.Id, f.Name, f.DateFrom)).ToList();
        return (notices, pushes);
    }

    public sealed class FestivalRow
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public DateOnly DateFrom { get; set; }
        public DateOnly DateTo { get; set; }
        public string? StateName { get; set; }
    }

    private sealed class MarkRow
    {
        public Guid ListingId { get; set; }
        public Guid UserId { get; set; }
        public bool Marked { get; set; }
    }
}
