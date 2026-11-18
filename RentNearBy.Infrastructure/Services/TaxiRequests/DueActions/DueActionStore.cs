using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services.TaxiRequests.DueActions;

// LeaseUntil doubles as the claim token: one claim statement stamps one now(), so a runner whose lease was reaped and re-claimed
// can never complete, fail or park the row of the newer runner.
public sealed record DueClaim(DueActionItem Item, DateTime LeaseUntil, double LagSeconds);

public sealed record DueFailResult(bool Applied, bool Parked);

public sealed record DueReapResult(int Reaped, int Parked)
{
    public static readonly DueReapResult None = new(0, 0);
}

public interface IDueActionStore
{
    Task<DueReapResult> ReapAsync(CancellationToken ct);
    Task<IReadOnlyList<DueClaim>> ClaimAsync(int batchSize, CancellationToken ct);
    // Seconds from the database clock to the earliest Pending row (negative = late); null when nothing is pending.
    Task<double?> PeekNextDueSecondsAsync(CancellationToken ct);
    Task<DueFailResult> FailAsync(DueClaim claim, string error, CancellationToken ct);
    Task<bool> ParkAsync(DueClaim claim, string error, CancellationToken ct);
    // Hands claimed but not started rows back (shutdown); no attempt is counted.
    Task<int> ReleaseAsync(IReadOnlyList<DueClaim> claims, CancellationToken ct);
}

public static class DueActionRules
{
    public const int BatchSize = 50, LeaseSeconds = 120, MaxAttempts = 8, MaxBackoffSeconds = 300, Parallelism = 4, MaxErrorLength = 500;
    public const double LagAlertSeconds = 30;
    public static readonly TimeSpan MaxSleep = TimeSpan.FromSeconds(10);

    public static TimeSpan SleepFor(double? secondsUntilNext)
    {
        if (secondsUntilNext is not { } s || double.IsNaN(s)) return MaxSleep;
        if (s <= 0) return TimeSpan.Zero;
        return s >= MaxSleep.TotalSeconds ? MaxSleep : TimeSpan.FromSeconds(s);
    }

    public static string TrimError(string error) => error.Length <= MaxErrorLength ? error : error[..MaxErrorLength];
}

// Every statement here is auto-commit. Every move to a final state sets DoneAt in the same statement (ck_dueactions_done).
public static class DueActionSql
{
    private const string Pending = DueActionStates.Pending, Running = DueActionStates.Running, Failed = DueActionStates.Failed;
    private const string NextAttempts = "\"Attempts\" + 1";
    private static readonly string ParkNow = $"{NextAttempts} >= {DueActionRules.MaxAttempts}";

    // Spelled exactly like the filter of ix_dueactions_due so the partial index serves the claim.
    public const string PendingDuePredicate = $"\"State\" = '{Pending}' AND \"DueAt\" <= now()";

    public static readonly string Claim = $"""
        WITH due AS (
            SELECT "Id" FROM "DueActions"
            WHERE {PendingDuePredicate}
            ORDER BY "DueAt", "Id" LIMIT @batch FOR UPDATE SKIP LOCKED)
        UPDATE "DueActions" d SET "State" = '{Running}', "LeaseUntil" = now() + make_interval(secs => {DueActionRules.LeaseSeconds})
        FROM due
        WHERE d."Id" = due."Id" AND d."State" = '{Pending}'
        RETURNING d."Id", d."Kind", d."SubjectId", d."RoundNo", d."RequestId", d."DueAt", d."Attempts", d."LeaseUntil",
            EXTRACT(EPOCH FROM (now() - d."DueAt"))::float8 AS "LagSeconds"
        """;

    // A crashed runner's row goes back to Pending and the crash counts as an attempt, so a row that kills its runner is parked too.
    public static readonly string Reap = $"""
        UPDATE "DueActions" SET
            "Attempts" = {NextAttempts},
            "State" = CASE WHEN {ParkNow} THEN '{Failed}' ELSE '{Pending}' END,
            "DoneAt" = CASE WHEN {ParkNow} THEN now() ELSE NULL END,
            "LastError" = CASE WHEN {ParkNow} THEN 'Lease expired' ELSE "LastError" END,
            "LeaseUntil" = NULL
        WHERE "State" = '{Running}' AND "LeaseUntil" < now()
        RETURNING "Id", "State"
        """;

    public const string Peek = $"""
        SELECT EXTRACT(EPOCH FROM (MIN("DueAt") - now()))::float8 AS "Seconds" FROM "DueActions" WHERE "State" = '{Pending}'
        """;

    private const string OwnClaim = $"\"Id\" = @id AND \"State\" = '{Running}' AND \"LeaseUntil\" = @lease";

    public const string StillClaimed = $"SELECT 1 AS \"Value\" FROM \"DueActions\" WHERE {OwnClaim}";

    // Done or Skipped, inside the item's own transaction.
    public const string Complete = $"UPDATE \"DueActions\" SET \"State\" = @state, \"DoneAt\" = now(), \"LeaseUntil\" = NULL WHERE {OwnClaim}";

    public static readonly string Fail = $"""
        UPDATE "DueActions" SET
            "Attempts" = {NextAttempts},
            "State" = CASE WHEN {ParkNow} THEN '{Failed}' ELSE '{Pending}' END,
            "DoneAt" = CASE WHEN {ParkNow} THEN now() ELSE NULL END,
            "DueAt" = CASE WHEN {ParkNow} THEN "DueAt" ELSE now() + make_interval(secs => LEAST(power(2::float8, {NextAttempts}), {DueActionRules.MaxBackoffSeconds})) END,
            "LastError" = @error,
            "LeaseUntil" = NULL
        WHERE {OwnClaim}
        RETURNING "State" AS "Value"
        """;

    public static readonly string Park = $"""
        UPDATE "DueActions" SET "Attempts" = {NextAttempts}, "State" = '{Failed}', "DoneAt" = now(), "LastError" = @error, "LeaseUntil" = NULL
        WHERE {OwnClaim}
        """;

    public const string Release = $"""
        UPDATE "DueActions" SET "State" = '{Pending}', "LeaseUntil" = NULL
        WHERE "Id" = ANY(@ids) AND "State" = '{Running}' AND "LeaseUntil" = @lease
        """;

    public static NpgsqlParameter IdParameter(Guid id) => new("id", NpgsqlDbType.Uuid) { Value = id };

    public static NpgsqlParameter LeaseParameter(DateTime lease) => new("lease", NpgsqlDbType.TimestampTz) { Value = lease };
}

// Each call opens its own short scope, so the store is safe to share across the parallel item runs.
public sealed class PostgresDueActionStore(IServiceScopeFactory scopeFactory) : IDueActionStore
{
    private sealed class ClaimRow
    {
        public Guid Id { get; set; }
        public string Kind { get; set; } = string.Empty;
        public Guid SubjectId { get; set; }
        public int RoundNo { get; set; }
        public Guid? RequestId { get; set; }
        public DateTime DueAt { get; set; }
        public short Attempts { get; set; }
        public DateTime LeaseUntil { get; set; }
        public double LagSeconds { get; set; }
    }

    private sealed class ReapRow
    {
        public Guid Id { get; set; }
        public string State { get; set; } = string.Empty;
    }

    private sealed class PeekRow
    {
        public double? Seconds { get; set; }
    }

    public Task<DueReapResult> ReapAsync(CancellationToken ct) => WithDbAsync(async db =>
    {
        var rows = await db.Database.SqlQueryRaw<ReapRow>(DueActionSql.Reap).ToListAsync(ct);
        return new DueReapResult(rows.Count, rows.Count(r => r.State == DueActionStates.Failed));
    });

    public Task<IReadOnlyList<DueClaim>> ClaimAsync(int batchSize, CancellationToken ct) => WithDbAsync<IReadOnlyList<DueClaim>>(async db =>
    {
        var rows = await db.Database
            .SqlQueryRaw<ClaimRow>(DueActionSql.Claim, new NpgsqlParameter("batch", NpgsqlDbType.Integer) { Value = batchSize })
            .ToListAsync(ct);
        return rows
            .OrderBy(r => r.DueAt).ThenBy(r => r.Id)
            .Select(r => new DueClaim(new DueActionItem(r.Id, r.Kind, r.SubjectId, r.RoundNo, r.RequestId, r.DueAt, r.Attempts), r.LeaseUntil, r.LagSeconds))
            .ToList();
    });

    public Task<double?> PeekNextDueSecondsAsync(CancellationToken ct) => WithDbAsync(async db =>
        (await db.Database.SqlQueryRaw<PeekRow>(DueActionSql.Peek).SingleAsync(ct)).Seconds);

    public Task<DueFailResult> FailAsync(DueClaim claim, string error, CancellationToken ct) => WithDbAsync(async db =>
    {
        var states = await db.Database.SqlQueryRaw<string>(DueActionSql.Fail,
            ErrorParameter(error), DueActionSql.IdParameter(claim.Item.Id), DueActionSql.LeaseParameter(claim.LeaseUntil)).ToListAsync(ct);
        return states.Count == 0 ? new DueFailResult(false, false) : new DueFailResult(true, states[0] == DueActionStates.Failed);
    });

    public Task<bool> ParkAsync(DueClaim claim, string error, CancellationToken ct) => WithDbAsync(async db =>
        await db.Database.ExecuteSqlRawAsync(DueActionSql.Park,
            [ErrorParameter(error), DueActionSql.IdParameter(claim.Item.Id), DueActionSql.LeaseParameter(claim.LeaseUntil)], ct) > 0);

    public Task<int> ReleaseAsync(IReadOnlyList<DueClaim> claims, CancellationToken ct) => WithDbAsync(async db =>
    {
        var released = 0;
        foreach (var group in claims.GroupBy(c => c.LeaseUntil))
        {
            released += await db.Database.ExecuteSqlRawAsync(DueActionSql.Release,
            [
                new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = group.Select(c => c.Item.Id).ToArray() },
                DueActionSql.LeaseParameter(group.Key),
            ], ct);
        }
        return released;
    });

    private static NpgsqlParameter ErrorParameter(string error)
        => new("error", NpgsqlDbType.Varchar) { Value = DueActionRules.TrimError(error) };

    private async Task<T> WithDbAsync<T>(Func<ApplicationDbContext, Task<T>> work)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        return await work(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>());
    }
}
