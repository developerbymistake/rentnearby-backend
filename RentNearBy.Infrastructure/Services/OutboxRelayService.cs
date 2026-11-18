using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services;

// In-process wake-up after a commit that wrote outbox rows; the 1 s poll is the correctness path.
public sealed class OutboxSignal : IOutboxSignal
{
    private readonly SemaphoreSlim _wake = new(0, 1);

    public void Wake()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { }
    }

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) => _wake.WaitAsync(timeout, ct);
}

public sealed class OutboxRow
{
    public long Id { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Exchange { get; set; } = string.Empty;
    public string RoutingKey { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public short Attempts { get; set; }
}

public sealed class OutboxBacklog
{
    public long Count { get; set; }
    public DateTime? Oldest { get; set; }
}

public sealed record OutboxFailure(long Id, short Attempts, DateTime NextAttemptAt, string Error);

public sealed record OutboxOutcome(IReadOnlyList<long> Published, IReadOnlyList<OutboxFailure> Failed);

// Pure part of the relay: rows in, confirmed ids and failures (with backoff) out. The DB claim and mark around it are thin.
public enum OutboxBacklogLevel { Ok, Warning, Critical }

// Claim, then publish with no transaction open, then mark. The claim leases rows by moving NextAttemptAt ahead.
public interface IOutboxLeaseStore
{
    bool HasOpenTransaction { get; }
    Task<IReadOnlyList<OutboxRow>> ClaimAsync(CancellationToken ct);
    Task MarkPublishedAsync(IReadOnlyList<long> ids, CancellationToken ct);
    Task MarkFailedAsync(IReadOnlyList<OutboxFailure> failures, CancellationToken ct);
}

public static class OutboxRelayLogic
{
    public const int BatchSize = 500, MaxBackoffSeconds = 300, UnavailableRetrySeconds = 15, LeaseSeconds = 60;

    // Same predicate in the CTE and in the UPDATE, so a row that changed between them is never leased.
    public const string PendingPredicate =
        "\"PublishedAt\" IS NULL AND \"NextAttemptAt\" <= now() AND \"CreatedAt\" > now() - make_interval(hours => {0})";

    // One auto-commit statement: {0} give-up hours, {1} batch size, {2} lease seconds.
    public const string ClaimSql = $$"""
        WITH due AS (
            SELECT "Id" FROM "OutboxMessages"
            WHERE {{PendingPredicate}}
            ORDER BY "NextAttemptAt", "Id" LIMIT {1} FOR UPDATE SKIP LOCKED)
        UPDATE "OutboxMessages" o SET "NextAttemptAt" = now() + make_interval(secs => {2})
        FROM due
        WHERE o."Id" = due."Id" AND {{PendingPredicate}}
        RETURNING o."Id", o."Key", o."Exchange", o."RoutingKey", o."Payload", o."Attempts"
        """;

    public static object[] ClaimParameters => [(int)GiveUpAfter.TotalHours, BatchSize, LeaseSeconds];

    public static DateTime LeaseUntil(DateTime nowUtc) => nowUtc.AddSeconds(LeaseSeconds);

    // In-memory form of PendingPredicate.
    public static bool IsClaimable(DateTime? publishedAtUtc, DateTime nextAttemptAtUtc, DateTime createdAtUtc, DateTime nowUtc)
        => publishedAtUtc is null && nextAttemptAtUtc <= nowUtc && nowUtc - createdAtUtc < GiveUpAfter;

    // A broker outage is not the message's fault: it never counts an attempt. The publisher marks such a failure with this prefix.
    public const string UnavailablePrefix = "broker unavailable: ";

    // A row lives as long as the enquiry it announces (72 h); after that the push would be stale, so it is left alone.
    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromHours(72);
    public static readonly TimeSpan WarnAge = TimeSpan.FromMinutes(5), CriticalAge = TimeSpan.FromMinutes(30);

    public static bool IsUnavailable(string? error) => error?.StartsWith(UnavailablePrefix, StringComparison.Ordinal) == true;

    public static bool IsGivenUp(DateTime createdAtUtc, DateTime nowUtc) => nowUtc - createdAtUtc > GiveUpAfter;

    public static OutboxBacklogLevel BacklogLevel(long count, DateTime? oldestCreatedUtc, DateTime nowUtc)
    {
        if (count <= 0 || oldestCreatedUtc is not { } oldest) return OutboxBacklogLevel.Ok;
        var age = nowUtc - oldest;
        return age > CriticalAge ? OutboxBacklogLevel.Critical : age > WarnAge ? OutboxBacklogLevel.Warning : OutboxBacklogLevel.Ok;
    }

    // Failure n waits 2^n seconds, capped at 5 min.
    public static TimeSpan Backoff(int attemptsAfterFailure)
        => TimeSpan.FromSeconds(Math.Min(MaxBackoffSeconds, Math.Pow(2, Math.Clamp(attemptsAfterFailure, 1, 30))));

    public static async Task<OutboxOutcome> PublishAsync(
        IReadOnlyList<OutboxRow> rows, IConfirmedPublisher publisher, DateTime nowUtc, CancellationToken ct)
    {
        var results = await publisher.PublishBatchAsync(
            rows.Select(r => new ConfirmedMessage(r.Exchange, r.RoutingKey, r.Payload, r.Key)).ToList(), ct);
        var published = new List<long>(rows.Count);
        var failed = new List<OutboxFailure>();
        for (var i = 0; i < rows.Count; i++)
        {
            var result = i < results.Count ? results[i] : new PublishResult(false, "no result");
            if (result.Confirmed)
            {
                published.Add(rows[i].Id);
                continue;
            }
            var error = result.Error ?? "publish failed";
            if (IsUnavailable(error))
            {
                failed.Add(new OutboxFailure(rows[i].Id, rows[i].Attempts, nowUtc.AddSeconds(UnavailableRetrySeconds), error));
                continue;
            }
            var attempts = (short)Math.Min(short.MaxValue, rows[i].Attempts + 1);
            failed.Add(new OutboxFailure(rows[i].Id, attempts, nowUtc + Backoff(attempts), error));
        }
        return new OutboxOutcome(published, failed);
    }

    // A crash after the claim leaves the rows leased; they are due again after LeaseSeconds (at least once, receivers dedupe by key).
    public static async Task<int> RelayOnceAsync(
        IOutboxLeaseStore store, IConfirmedPublisher publisher, Func<DateTime> utcNow, CancellationToken ct)
    {
        EnsureNoTransaction(store);
        var rows = await store.ClaimAsync(ct);
        if (rows.Count == 0) return 0;
        EnsureNoTransaction(store);

        OutboxOutcome outcome;
        try
        {
            outcome = await PublishAsync(rows, publisher, utcNow(), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var retry = utcNow().AddSeconds(UnavailableRetrySeconds);
            outcome = new OutboxOutcome([], rows.Select(r => new OutboxFailure(r.Id, r.Attempts, retry, UnavailablePrefix + ex.Message)).ToList());
        }

        // Published messages are recorded even during shutdown, so they are not sent again after the lease.
        if (outcome.Published.Count > 0) await store.MarkPublishedAsync(outcome.Published, CancellationToken.None);
        if (outcome.Failed.Count > 0) await store.MarkFailedAsync(outcome.Failed, CancellationToken.None);
        return rows.Count;
    }

    private static void EnsureNoTransaction(IOutboxLeaseStore store)
    {
        if (store.HasOpenTransaction)
            throw new InvalidOperationException("The outbox relay must not run inside a database transaction.");
    }
}

public sealed class PostgresOutboxLeaseStore(ApplicationDbContext db) : IOutboxLeaseStore
{
    public bool HasOpenTransaction => db.Database.CurrentTransaction is not null || System.Transactions.Transaction.Current is not null;

    public async Task<IReadOnlyList<OutboxRow>> ClaimAsync(CancellationToken ct)
        => await db.Database.SqlQueryRaw<OutboxRow>(OutboxRelayLogic.ClaimSql, OutboxRelayLogic.ClaimParameters).ToListAsync(ct);

    public Task MarkPublishedAsync(IReadOnlyList<long> ids, CancellationToken ct)
    {
        var idArray = ids.ToArray();
        return db.Database.ExecuteSqlInterpolatedAsync(
            $"""UPDATE "OutboxMessages" SET "PublishedAt" = now(), "LastError" = NULL WHERE "Id" = ANY({idArray})""", ct);
    }

    public Task MarkFailedAsync(IReadOnlyList<OutboxFailure> failures, CancellationToken ct)
    {
        var ids = failures.Select(f => f.Id).ToArray();
        var attempts = failures.Select(f => f.Attempts).ToArray();
        var next = failures.Select(f => f.NextAttemptAt).ToArray();
        var errors = failures.Select(f => f.Error).ToArray();
        return db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "OutboxMessages" o SET "Attempts" = f.a, "NextAttemptAt" = f.n, "LastError" = f.e
            FROM unnest({ids}, {attempts}, {next}, {errors}) AS f(id, a, n, e)
            WHERE o."Id" = f.id AND o."PublishedAt" IS NULL
            """, ct);
    }
}

// Publishes OutboxMessages rows (push rows from trip writers and the reminder run) one message per recipient with broker confirms.
// The lease claim (FOR UPDATE SKIP LOCKED, auto-commit) keeps it safe on any number of instances; request threads never touch the broker.
public sealed class OutboxRelayService(
    IServiceScopeFactory scopeFactory, IConfirmedPublisher publisher, OutboxSignal signal, ILogger<OutboxRelayService> logger,
    WorkerHeartbeats? heartbeats = null)
    : BackgroundService
{
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(1), ErrorPause = TimeSpan.FromSeconds(5), BacklogEvery = TimeSpan.FromSeconds(60);
    private DateTime _nextBacklogCheck;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                heartbeats?.Beat(WorkerNames.OutboxRelay);
                var more = false;
                try
                {
                    more = await RelayBatchAsync(stoppingToken) >= OutboxRelayLogic.BatchSize;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Outbox relay batch failed");
                    await Task.Delay(ErrorPause, stoppingToken);
                    continue;
                }
                await CheckBacklogAsync(stoppingToken);
                if (!more) await signal.WaitAsync(Poll, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // Count and age of rows still waiting, once a minute. Warning from 5 min, Critical from 30 min or when a row passed the give-up age.
    // Alert on the "outbox.backlog" log event (see SCALE_RUNBOOK.md).
    public async Task<OutboxBacklogLevel> CheckBacklogAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if (now < _nextBacklogCheck) return OutboxBacklogLevel.Ok;
        _nextBacklogCheck = now + BacklogEvery;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var stat = await db.Database.SqlQuery<OutboxBacklog>(
                $"""SELECT COUNT(*) AS "Count", MIN("CreatedAt") AS "Oldest" FROM "OutboxMessages" WHERE "PublishedAt" IS NULL""").SingleAsync(ct);
            var level = OutboxRelayLogic.BacklogLevel(stat.Count, stat.Oldest, now);
            var givenUp = stat.Oldest is { } o && OutboxRelayLogic.IsGivenUp(o, now);
            if (givenUp) level = OutboxBacklogLevel.Critical;
            var ageMin = stat.Oldest is { } old ? (now - old).TotalMinutes : 0;
            if (level == OutboxBacklogLevel.Critical)
                logger.LogCritical(new EventId(7001, "outbox.backlog"), "Outbox backlog {Count} unpublished, oldest {AgeMinutes:F0} min, given up rows present {GivenUp}", stat.Count, ageMin, givenUp);
            else if (level == OutboxBacklogLevel.Warning)
                logger.LogWarning(new EventId(7001, "outbox.backlog"), "Outbox backlog {Count} unpublished, oldest {AgeMinutes:F0} min", stat.Count, ageMin);
            return level;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Outbox backlog check failed");
            return OutboxBacklogLevel.Ok;
        }
    }

    public async Task<int> RelayBatchAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var store = new PostgresOutboxLeaseStore(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>());
        return await OutboxRelayLogic.RelayOnceAsync(store, publisher, () => DateTime.UtcNow, ct);
    }
}
