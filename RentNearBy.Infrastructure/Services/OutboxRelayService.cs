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

public static class OutboxRelayLogic
{
    public const int BatchSize = 500, MaxBackoffSeconds = 300, UnavailableRetrySeconds = 15;

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
}

// Publishes OutboxMessages rows (push rows from trip writers and the reminder run) one message per recipient with broker confirms.
// SELECT .. FOR UPDATE SKIP LOCKED keeps it safe on any number of instances; request threads never touch the broker.
public sealed class OutboxRelayService(
    IServiceScopeFactory scopeFactory, IConfirmedPublisher publisher, OutboxSignal signal, ILogger<OutboxRelayService> logger)
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
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var rows = await db.Database.SqlQuery<OutboxRow>($"""
            SELECT "Id", "Key", "Exchange", "RoutingKey", "Payload", "Attempts"
            FROM "OutboxMessages"
            WHERE "PublishedAt" IS NULL AND "NextAttemptAt" <= now() AND "CreatedAt" > now() - make_interval(hours => {(int)OutboxRelayLogic.GiveUpAfter.TotalHours})
            ORDER BY "NextAttemptAt", "Id" LIMIT {OutboxRelayLogic.BatchSize} FOR UPDATE SKIP LOCKED
            """).ToListAsync(ct);
        if (rows.Count == 0)
        {
            await tx.RollbackAsync(ct);
            return 0;
        }

        var outcome = await OutboxRelayLogic.PublishAsync(rows, publisher, DateTime.UtcNow, ct);
        if (outcome.Published.Count > 0)
        {
            var ids = outcome.Published.ToArray();
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""UPDATE "OutboxMessages" SET "PublishedAt" = now(), "LastError" = NULL WHERE "Id" = ANY({ids})""", ct);
        }
        if (outcome.Failed.Count > 0)
        {
            var ids = outcome.Failed.Select(f => f.Id).ToArray();
            var attempts = outcome.Failed.Select(f => f.Attempts).ToArray();
            var next = outcome.Failed.Select(f => f.NextAttemptAt).ToArray();
            var errors = outcome.Failed.Select(f => f.Error).ToArray();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "OutboxMessages" o SET "Attempts" = f.a, "NextAttemptAt" = f.n, "LastError" = f.e
                FROM unnest({ids}, {attempts}, {next}, {errors}) AS f(id, a, n, e)
                WHERE o."Id" = f.id
                """, ct);
        }
        await tx.CommitAsync(ct);
        return rows.Count;
    }
}
