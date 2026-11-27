using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Entities.Buddy;
using RentNearBy.Core.Entities.Social;
using RentNearBy.Core.Entities.TaxiRequests;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Core.Models.Social;
using RentNearBy.Core.Models.TaxiRequests;

namespace RentNearBy.Infrastructure.Services.Retention.GroupB;

public sealed record TripCompactionCounts(long Eligible, long PlanBytes, long DraftsWithoutEnd);

public sealed record TripCompactionOutcome(bool Archived, long BytesSaved);

// The database side of the compaction, behind a seam so the batch logic (dry run, skip list, log) is tested without PostgreSQL.
public interface ITripCompactionStore
{
    Task<long> TableRowsAsync(RetentionContext ctx, CancellationToken ct);
    Task<long> CountCappedAsync(RetentionContext ctx, long cap, CancellationToken ct);
    Task<TripCompactionCounts> CountAsync(RetentionContext ctx, CancellationToken ct);
    Task<IReadOnlyList<Guid>> NextBatchAsync(RetentionContext ctx, int n, IReadOnlyCollection<Guid> skip, CancellationToken ct);
    // One trip, one transaction; false when the trip is no longer eligible or another worker holds its row.
    Task<TripCompactionOutcome> ArchiveAsync(RetentionContext ctx, Guid tripId, CancellationToken ct);
}

// Trip compaction (owner order 2026-11-27, Buddy_resume/trip_compaction/SPEC.md): 90 days after a finished trip's end its
// heavy PlanJson is replaced by a small summary and the leftovers with no value after the trip are deleted. It is an UPDATE
// of Trips (never a delete of the trip). Dry run is the default until Retention:Rules:TripCompaction:DryRun is false;
// Retention:Rules:TripCompaction:Enabled false is the kill switch. Never touches the trip's completion stamp or the
// users' completed trip count (guarded by TripCompactionRuleTests).
public sealed class TripCompactionRule : IDryRunRetentionRule
{
    public const string RuleName = "TripCompaction";
    // Trips per batch (each in its own transaction); the executor's row batch size is clamped to this.
    public const int TripBatch = 25;

    private sealed class RunState
    {
        public bool Started;
        public bool Done;
        public long Eligible;
        public long Archived;
        public long BytesSaved;
        public readonly HashSet<Guid> Failed = new();
        // Failed plus the ids that were not archived this run (locked elsewhere or no longer eligible): never fetched again.
        public readonly HashSet<Guid> Skip = new();
    }

    private readonly Func<RetentionContext, ITripCompactionStore> _store;
    // One state per run: the executor builds one context per run and passes the same instance to every batch.
    private readonly ConditionalWeakTable<RetentionContext, RunState> _runs = new();

    public TripCompactionRule() : this(_ => new TripCompactionStore()) { }

    public TripCompactionRule(Func<RetentionContext, ITripCompactionStore> store) { _store = store; }

    public string Name => RuleName;
    public string Group => RetentionGroups.TripHousekeeping;
    public int DefaultDays => 90;
    public bool DefaultEnabled => true;
    public bool GuardExempt => false;
    public bool DefaultDryRun => true;

    public Task<long> EstimateTableRowsAsync(RetentionContext ctx, CancellationToken ct) => _store(ctx).TableRowsAsync(ctx, ct);

    public Task<long> CountCandidatesAsync(RetentionContext ctx, long cap, CancellationToken ct) => _store(ctx).CountCappedAsync(ctx, cap, ct);

    // Returns the number of trips archived in this batch; 0 ends the run. A dry run counts once, logs and returns 0.
    public async Task<int> DeleteBatchAsync(RetentionContext ctx, int batchSize, CancellationToken ct)
    {
        var run = _runs.GetValue(ctx, _ => new RunState());
        if (run.Done) return 0;
        var store = _store(ctx);
        if (!run.Started)
        {
            run.Started = true;
            var counts = await store.CountAsync(ctx, ct);
            run.Eligible = counts.Eligible;
            ctx.Log?.LogInformation(
                "TRIP_COMPACTION start rule={Rule} dryRun={DryRun} days={Days} cutoffDate={CutoffDate} eligible={Eligible} planBytes={PlanBytes} draftsWithoutEnd={Drafts}",
                RuleName, ctx.DryRun, ctx.Days, ctx.CutoffDate, counts.Eligible, counts.PlanBytes, counts.DraftsWithoutEnd);
            if (ctx.DryRun) return Finish(ctx, run);
        }

        // A batch where nothing was archived (every trip failed, was locked by another worker or stopped being eligible) does
        // not end the run: those ids join the skip list and the next ids are tried, so a block of always-failing trips at the
        // head of the EndDate order can never starve the trips behind it night after night. Ends: every id is either archived
        // (no longer eligible) or skipped, so each round removes at least one id from the candidates.
        while (true)
        {
            var ids = await store.NextBatchAsync(ctx, Math.Clamp(batchSize, 1, TripBatch), run.Skip, ct);
            if (ids.Count == 0) return Finish(ctx, run);

            var archived = 0;
            foreach (var id in ids)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var outcome = await store.ArchiveAsync(ctx, id, ct);
                    if (!outcome.Archived)
                    {
                        run.Skip.Add(id);
                        continue;
                    }
                    archived++;
                    run.Archived++;
                    run.BytesSaved += outcome.BytesSaved;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One bad trip never blocks the rest: it is skipped for the rest of this run and retried next night.
                    run.Failed.Add(id);
                    run.Skip.Add(id);
                    ctx.Log?.LogWarning(ex, "TRIP_COMPACTION trip failed rule={Rule} tripId={TripId}", RuleName, id);
                }
            }
            if (archived > 0) return archived;
        }
    }

    private static int Finish(RetentionContext ctx, RunState run)
    {
        if (run.Done) return 0;
        run.Done = true;
        ctx.Log?.LogInformation(
            "TRIP_COMPACTION done rule={Rule} dryRun={DryRun} eligible={Eligible} archived={Archived} failed={Failed} bytesSaved={BytesSaved}",
            RuleName, ctx.DryRun, run.Eligible, run.Archived, run.Failed.Count, run.BytesSaved);
        return 0;
    }
}

public sealed class TripCompactionStore : ITripCompactionStore
{
    // Statuses still in negotiation: a trip with one of them waits (their own clean-up closes them first).
    public const string NegotiatingStatuses = "'Asked','Offered','Countered','FareSent'";

    // The exact eligibility (alias t). Parameters: @cutoffDate = today IST minus Days (90), @cutoff = now minus Days. Drafts are
    // never touched. A trip whose group chat is still in use (a message in the last Days) waits until the chat is idle as long,
    // the same idle rule the direct rooms follow (DirectChatRooms), so a group that still talks never loses its room.
    public static readonly string EligibleSql =
        $"""
        t."ArchivedAt" IS NULL AND t."EndDate" IS NOT NULL AND t."EndDate" < @cutoffDate
        AND t."Status" IN ('{TripStatuses.Completed}','{TripStatuses.Planned}')
        AND NOT EXISTS (SELECT 1 FROM "TripEnquiries" e WHERE e."TripId" = t."Id" AND e."Status" IN ({NegotiatingStatuses}))
        AND NOT EXISTS (SELECT 1 FROM "TaxiRequests" r WHERE r."TripId" = t."Id" AND r."Status" IN ({RequestStates.ActiveSqlList}))
        AND NOT EXISTS (SELECT 1 FROM "TaxiBookings" b WHERE b."TripId" = t."Id" AND b."Status" IN ({BookingStates.ActiveSqlList}))
        AND NOT EXISTS (SELECT 1 FROM "ChatRooms" g WHERE g."TripId" = t."Id" AND g."Kind" = '{ChatKinds.Trip}' AND g."LastMessageAt" >= @cutoff)
        AND NOT EXISTS (SELECT 1 FROM "ListingReports" p WHERE p."Status" = 'Pending' AND (
            (p."ListingType" = '{SocialReportTypes.TripPost}' AND p."ListingId" = t."Id")
            OR (p."ListingType" = '{SocialReportTypes.Message}' AND p."ListingId" IN (SELECT c."Id" FROM "ChatRooms" c WHERE c."TripId" = t."Id"))))
        """;

    public static readonly string CountSql =
        $"""
        SELECT count(*)::bigint AS "Eligible", COALESCE(sum(octet_length(t."PlanJson"::text)), 0)::bigint AS "PlanBytes",
               (SELECT count(*)::bigint FROM "Trips" d WHERE d."ArchivedAt" IS NULL AND d."Status" = '{TripStatuses.Draft}' AND d."EndDate" IS NULL) AS "DraftsWithoutEnd"
        FROM "Trips" t WHERE ({EligibleSql})
        """;

    public static string CappedCountSql(long cap) =>
        $"""SELECT count(*)::bigint AS "Value" FROM (SELECT 1 FROM "Trips" t WHERE ({EligibleSql}) LIMIT {cap + 1}) x""";

    public static readonly string NextBatchSql =
        $"""
        SELECT t."Id" AS "Value" FROM "Trips" t
        WHERE ({EligibleSql}) AND NOT (t."Id" = ANY(@skip))
        ORDER BY t."EndDate", t."Id"
        LIMIT @n
        """;

    // Re-checks the eligibility under the row lock; another worker's lock skips the trip (retried next night).
    public static readonly string LockSql =
        $"""SELECT t."Id" AS "Value" FROM "Trips" t WHERE t."Id" = @trip AND ({EligibleSql}) FOR UPDATE OF t SKIP LOCKED""";

    private sealed class CountsRow
    {
        public long Eligible { get; set; }
        public long PlanBytes { get; set; }
        public long DraftsWithoutEnd { get; set; }
    }

    public async Task<long> TableRowsAsync(RetentionContext ctx, CancellationToken ct)
    {
        var est = await ctx.Db.Database
            .SqlQueryRaw<long>("SELECT COALESCE((SELECT reltuples::bigint FROM pg_class WHERE oid = to_regclass('\"Trips\"')), -1) AS \"Value\"")
            .SingleAsync(ct);
        if (est >= DeclarativeRetentionRule.ExactCountBelow) return est;
        return await ctx.Db.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM \"Trips\"").SingleAsync(ct);
    }

    public Task<long> CountCappedAsync(RetentionContext ctx, long cap, CancellationToken ct)
    {
        var sql = CappedCountSql(cap);
        return ctx.Db.Database.SqlQueryRaw<long>(sql, Params(sql, ctx, 0)).SingleAsync(ct);
    }

    public async Task<TripCompactionCounts> CountAsync(RetentionContext ctx, CancellationToken ct)
    {
        var row = await ctx.Db.Database.SqlQueryRaw<CountsRow>(CountSql, Params(CountSql, ctx, 0)).SingleAsync(ct);
        return new TripCompactionCounts(row.Eligible, row.PlanBytes, row.DraftsWithoutEnd);
    }

    public async Task<IReadOnlyList<Guid>> NextBatchAsync(RetentionContext ctx, int n, IReadOnlyCollection<Guid> skip, CancellationToken ct)
    {
        var ps = new List<object>(Params(NextBatchSql, ctx, n))
        {
            new NpgsqlParameter("skip", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = skip.ToArray() },
        };
        return await ctx.Db.Database.SqlQueryRaw<Guid>(NextBatchSql, ps.ToArray()).ToListAsync(ct);
    }

    public async Task<TripCompactionOutcome> ArchiveAsync(RetentionContext ctx, Guid tripId, CancellationToken ct)
    {
        var db = ctx.Db;
        var now = DateTime.SpecifyKind(ctx.NowUtc, DateTimeKind.Utc);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var lockParams = new List<object>(Params(LockSql, ctx, 0)) { new NpgsqlParameter("trip", NpgsqlDbType.Uuid) { Value = tripId } };
        var locked = await db.Database.SqlQueryRaw<Guid>(LockSql, lockParams.ToArray()).ToListAsync(ct);
        if (locked.Count == 0)
        {
            await tx.RollbackAsync(ct);
            return new TripCompactionOutcome(false, 0);
        }

        var trip = await db.Trips.AsNoTracking().Where(t => t.Id == tripId).Select(t => new Trip
        {
            Id = t.Id, Title = t.Title, Status = t.Status, CityKey = t.CityKey, CityName = t.CityName, CityLatitude = t.CityLatitude,
            CityLongitude = t.CityLongitude, DistrictId = t.DistrictId, PlanJson = t.PlanJson, DayCount = t.DayCount, SpotCount = t.SpotCount,
            StartDate = t.StartDate, EndDate = t.EndDate,
        }).SingleAsync(ct);

        var stays = await db.TripEnquiries.AsNoTracking()
            .Where(e => e.TripId == tripId && e.Kind == TripEnquiryKinds.Stay && TripEnquiryStatuses.Booked.Contains(e.Status))
            .Select(e => new TripEnquiry
            {
                Id = e.Id, TripId = e.TripId, Kind = e.Kind, Status = e.Status, ListingName = e.ListingName, ContextJson = e.ContextJson,
                ListingLatitude = e.ListingLatitude, ListingLongitude = e.ListingLongitude, CheckIn = e.CheckIn, CheckOut = e.CheckOut,
                CreatedAt = e.CreatedAt,
            })
            .ToListAsync(ct);
        var taxiBooked =
            await db.TripEnquiries.AsNoTracking().AnyAsync(e => e.TripId == tripId && e.Kind == TripEnquiryKinds.Taxi && TripEnquiryStatuses.Booked.Contains(e.Status), ct)
            || await db.Set<TaxiBooking>().AsNoTracking().AnyAsync(b => b.TripId == tripId && b.Status != BookingStates.Cancelled, ct);
        var members = await db.Set<TripMember>().AsNoTracking().CountAsync(m => m.TripId == tripId && m.Status == TripMemberStatuses.Joined, ct);
        var expenses = await db.Set<TripExpense>().AsNoTracking().Where(e => e.TripId == tripId && e.DeletedAt == null)
            .SumAsync(e => (long?)e.Amount, ct);

        var summary = TripArchiveSummaryBuilder.Build(new TripArchiveInput(trip, stays, taxiBooked, members, expenses));
        var summaryJson = TripArchive.Serialize(summary);
        var emptyPlan = TripArchive.EmptyPlanJson;

        // Only these columns change. The completion stamp, CompletedAt, Status and every user row stay as they are.
        // UpdatedAt moves so every cached copy (viewer ETag, app copies) refreshes and sees the archived trip.
        var updated = await db.Trips.Where(t => t.Id == tripId && t.ArchivedAt == null).ExecuteUpdateAsync(s => s
            .SetProperty(t => t.SummaryJson, summaryJson)
            .SetProperty(t => t.PlanJson, emptyPlan)
            .SetProperty(t => t.ArchivedAt, now)
            .SetProperty(t => t.IsPublic, false)
            .SetProperty(t => t.PausedByPlan, false)
            .SetProperty(t => t.UpdatedAt, now), ct);
        if (updated == 0)
        {
            await tx.RollbackAsync(ct);
            return new TripCompactionOutcome(false, 0);
        }

        // Leftovers with no value after the trip. The trip group chat room goes with its members and messages (FK cascade).
        // Kept: TripMembers, invites, expenses and their shares/payments, enquiries, offers, taxi requests and bookings
        // (their own retention rules decide), problem logs, reports.
        await db.Set<ChatRoom>().Where(r => r.TripId == tripId && r.Kind == ChatKinds.Trip).ExecuteDeleteAsync(ct);
        await db.TripShortlistItems.Where(i => i.TripId == tripId).ExecuteDeleteAsync(ct);
        await db.Set<TripJoinRequest>().Where(j => j.TripId == tripId).ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);

        var saved = Encoding.UTF8.GetByteCount(trip.PlanJson ?? string.Empty) - Encoding.UTF8.GetByteCount(summaryJson);
        return new TripCompactionOutcome(true, Math.Max(0, saved));
    }

    private static object[] Params(string sql, RetentionContext ctx, int n) => RetentionSqlParams.For(sql, ctx, n).Cast<object>().ToArray();
}
