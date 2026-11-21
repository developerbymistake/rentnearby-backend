using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Interfaces.Buddy;
using RentNearBy.Core.Interfaces.Owner;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.TaxiRequests;
using static RentNearBy.Infrastructure.Services.Owner.Be4.OwnerJobSql;

namespace RentNearBy.Infrastructure.Services.Owner.Be4;

// Contract 5.1: the daily calendar reminder (drivers 07:00 IST: trips today; stay owners 08:00 IST: check-ins today). Run row per
// (IST day, kind) inserted ON CONFLICT DO NOTHING, a finished row = return. Per batch of listings (keyset by Id from LastListingId),
// one short transaction: (1) ONE select (Approved, not deleted, RemindersOn, plan value Reminders = 1, active owner with a valid
// device token, today's rows as JSON), (2) ONE outbox insert, (3) the run row update. Skipped when it would start after the late limit.
public abstract class OwnerMorningReminderJob(
    ApplicationDbContext db,
    IOwnerNotifier notifier,
    IPlanCatalogCache catalog,
    IPaymentFeatureReader payment,
    IOptions<OwnerFeatureOptions> options,
    TimeProvider clock,
    ILogger logger) : IBuddyFeatureJob
{
    protected abstract string Kind { get; }

    protected abstract string RunsAtText { get; }

    // SQL predicate over CalendarEntries alias e for the rows the reminder lists today.
    protected abstract string TodayPredicate { get; }

    protected abstract OwnerPush Build(Guid userId, Guid listingId, DateOnly today, IReadOnlyList<OwnerReminderLine> lines);

    public abstract string Name { get; }

    public TimeSpan Interval => TimeSpan.FromDays(1);

    public TimeOnly? DailyAtIst => ParseIstTime(RunsAtText) ?? new TimeOnly(8, 0);

    protected OwnerFeatureOptions Options => options.Value;

    internal const string StartRunSql = """
        INSERT INTO "OwnerReminderRuns" ("RunDate", "ListingKind", "StartedAt", "Sent") VALUES (@day, @kind, now(), 0)
        ON CONFLICT ("RunDate", "ListingKind") DO NOTHING
        """;

    internal const string ReadRunSql = """
        SELECT "FinishedAt", "LastListingId", "Sent" FROM "OwnerReminderRuns" WHERE "RunDate" = @day AND "ListingKind" = @kind
        """;

    internal const string StepRunSql = """
        UPDATE "OwnerReminderRuns" SET "LastListingId" = @last, "Sent" = "Sent" + @sent WHERE "RunDate" = @day AND "ListingKind" = @kind
        """;

    internal const string FinishRunSql = """
        UPDATE "OwnerReminderRuns" SET "FinishedAt" = now() WHERE "RunDate" = @day AND "ListingKind" = @kind AND "FinishedAt" IS NULL
        """;

    public string BatchSql(string cte) => $"""
        WITH {cte}
        SELECT l."Id" AS "ListingId", l."UserId",
               (SELECT json_agg(json_build_object('kind', e."Kind", 'label', e."Label", 'detail', e."Detail", 'time', to_char(e."TimeOfDay", 'HH24:MI'))
                                ORDER BY e."TimeOfDay" NULLS FIRST, e."DateFrom", e."Id")
                FROM "CalendarEntries" e
                WHERE e."ListingKind" = '{Kind}' AND e."ListingId" = l."Id" AND NOT e."Cancelled" AND {TodayPredicate})::text AS "Lines"
        FROM {Table(Kind)} l
        JOIN "Users" u ON u."Id" = l."UserId" AND u."IsActive"
        {PlanJoin("l", Kind)}
        WHERE l."Id" > @after AND l."Status" = 'Approved' AND NOT l."IsDeleted" AND l."RemindersOn"
          AND {PlanKeySql.Column(PlanFeatureKeys.Reminders)} = 1
          AND {HasValidToken("l")}
        ORDER BY l."Id"
        LIMIT @n
        """;

    public async Task<int> RunAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var today = IstTime.TodayIst(now);
        var at = DailyAtIst!.Value.ToTimeSpan();
        if (IstTime.TimeOfDayIst(now) > at + TimeSpan.FromHours(Math.Max(1, Options.ReminderLateHours)))
        {
            logger.LogInformation("owner.reminder {Kind} skipped: too late for {Day}", Kind, today);
            return 0;
        }

        await db.Database.ExecuteSqlRawAsync(StartRunSql, [Date("day", today), Text("kind", Kind)], ct);
        var run = (await db.Database.SqlQueryRaw<RunRow>(ReadRunSql, Date("day", today), Text("kind", Kind)).ToListAsync(ct)).FirstOrDefault();
        if (run is null || run.FinishedAt != null) return 0;

        var snapshot = await catalog.GetAsync();
        var flag = await payment.GetAsync();
        var sql = BatchSql(PlanKeySql.Cte(snapshot, flag, Kind));
        var batch = Math.Clamp(Options.JobBatch, 1, 5000);
        var after = run.LastListingId ?? Guid.Empty;
        var sent = 0;
        while (true) // batch-loop
        {
            var cursor = after;
            var (rows, written) = await PgRetry.OnceAsync(db, async c =>
            {
                await using var tx = await db.Database.BeginTransactionAsync(c);
                var listings = await db.Database.SqlQueryRaw<ListingRow>(sql, Uuid("after", cursor), Int("n", batch), Date("today", today)).ToListAsync(c);
                var pushes = Plan(listings, today);
                if (listings.Count > 0)
                    await db.Database.ExecuteSqlRawAsync(StepRunSql,
                        [Uuid("last", listings[^1].ListingId), Int("sent", pushes.Count), Date("day", today), Text("kind", Kind)], c);
                await notifier.PushAsync(pushes, c);
                await tx.CommitAsync(c);
                return (listings, pushes.Count);
            }, ct, logger);
            sent += written;
            if (rows.Count < batch)
            {
                await db.Database.ExecuteSqlRawAsync(FinishRunSql, [Date("day", today), Text("kind", Kind)], ct);
                break;
            }
            after = rows[^1].ListingId;
        }
        if (sent > 0) notifier.Wake();
        return sent;
    }

    // Pure: one push per listing that has at least one row today.
    public IReadOnlyList<OwnerPush> Plan(IReadOnlyList<ListingRow> listings, DateOnly today)
        => listings.Select(l => (l, Lines: ParseLines(l.Lines)))
            .Where(x => x.Lines.Count > 0)
            .Select(x => Build(x.l.UserId, x.l.ListingId, today, x.Lines))
            .ToList();

    public static IReadOnlyList<OwnerReminderLine> ParseLines(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
            var lines = new List<OwnerReminderLine>();
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                lines.Add(new OwnerReminderLine(
                    Str(e, "kind") ?? CalendarEntryKinds.Manual,
                    Str(e, "label") ?? string.Empty,
                    Str(e, "detail"),
                    ParseIstTime(Str(e, "time"))));
            }
            return lines;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public sealed class ListingRow
    {
        public Guid ListingId { get; set; }
        public Guid UserId { get; set; }
        public string? Lines { get; set; }
    }

    private sealed class RunRow
    {
        public DateTime? FinishedAt { get; set; }
        public Guid? LastListingId { get; set; }
        public int Sent { get; set; }
    }
}

// Stay owners, 08:00 IST: today's check-ins (Auto or Manual, not cancelled).
public sealed class StayMorningReminderJob(
    ApplicationDbContext db, IOwnerNotifier notifier, IPlanCatalogCache catalog, IPaymentFeatureReader payment,
    IOptions<OwnerFeatureOptions> options, TimeProvider clock, ILogger<StayMorningReminderJob> logger)
    : OwnerMorningReminderJob(db, notifier, catalog, payment, options, clock, logger)
{
    public override string Name => "owner.reminder.stay";

    protected override string Kind => ListingKinds.Stay;

    protected override string RunsAtText => Options.StayReminderIst;

    protected override string TodayPredicate => "e.\"DateFrom\" = @today";

    protected override OwnerPush Build(Guid userId, Guid listingId, DateOnly today, IReadOnlyList<OwnerReminderLine> lines)
        => StayOwnerPushCopy.MorningCheckIns(userId, listingId, today, lines);
}

// Drivers, 07:00 IST: every trip that runs today.
public sealed class TaxiMorningReminderJob(
    ApplicationDbContext db, IOwnerNotifier notifier, IPlanCatalogCache catalog, IPaymentFeatureReader payment,
    IOptions<OwnerFeatureOptions> options, TimeProvider clock, ILogger<TaxiMorningReminderJob> logger)
    : OwnerMorningReminderJob(db, notifier, catalog, payment, options, clock, logger)
{
    public override string Name => "owner.reminder.taxi";

    protected override string Kind => ListingKinds.Taxi;

    protected override string RunsAtText => Options.TaxiReminderIst;

    protected override string TodayPredicate => "e.\"DateFrom\" <= @today AND e.\"DateTo\" >= @today";

    protected override OwnerPush Build(Guid userId, Guid listingId, DateOnly today, IReadOnlyList<OwnerReminderLine> lines)
        => TaxiDriverPushCopy.MorningTrips(userId, listingId, today, lines);
}
