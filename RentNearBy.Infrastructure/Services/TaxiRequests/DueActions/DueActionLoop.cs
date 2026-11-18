using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services.TaxiRequests.DueActions;

// One item = one DI scope + one READ COMMITTED transaction. The handler's rows (state changes, outbox, inbox) and the item's
// Done/Skipped mark commit together; disposing without CommitAsync rolls everything back.
public interface IDueActionItemScope : IAsyncDisposable
{
    ApplicationDbContext Db { get; }
    IDueActionHandler? HandlerFor(string kind);
    // Plain read, no lock: domain rows are locked before the due action row (lock order of MASTER_PLAN 2.4).
    Task<bool> IsStillClaimedAsync(DueClaim claim, CancellationToken ct);
    // Marks the row, then saves the handler's tracked rows. False = the claim was lost; the caller must not commit.
    Task<bool> CompleteAsync(DueClaim claim, DueOutcome outcome, CancellationToken ct);
    Task CommitAsync(CancellationToken ct);
    // After the commit only: wakes the outbox relay. The loop guards it, so a throw never changes the item's outcome.
    void SignalCommitted();
}

public interface IDueActionItemScopeFactory
{
    Task<IDueActionItemScope> BeginAsync(CancellationToken ct);
}

// Optional for a handler: runs after its transaction committed (signals, realtime hints). Failures are logged, never retried.
public interface IDueActionAfterCommit
{
    Task AfterCommitAsync(DueActionItem item, DueOutcome outcome, CancellationToken ct);
}

public enum DueItemResult
{
    Done,
    Skipped,
    Stale,
    Failed,
    Parked,
    Released,
}

public sealed record DuePassResult(int Claimed, TimeSpan Sleep, IReadOnlyList<DueItemResult> Results);

public sealed class DueActionLoop(
    IDueActionStore store, IDueActionItemScopeFactory scopes, IDueActionsWaiter signal, ILogger logger,
    TaxiRequestMetrics? metrics = null, Func<DateTime>? utcNow = null)
{
    private static readonly TimeSpan LagLogEvery = TimeSpan.FromSeconds(60);
    private readonly Func<DateTime> _utcNow = utcNow ?? (() => DateTime.UtcNow);
    private DateTime _nextLagLog = DateTime.MinValue;

    public async Task<DuePassResult> RunOnceAsync(CancellationToken ct)
    {
        var reap = await store.ReapAsync(ct);
        if (reap.Parked > 0)
            logger.LogError(TaxiRequestLogEvents.DueParked, "{Count} due actions parked after their lease expired {Limit} times", reap.Parked, DueActionRules.MaxAttempts);

        var claims = await store.ClaimAsync(DueActionRules.BatchSize, ct);
        foreach (var claim in claims) metrics?.RecordDueLag(claim.LagSeconds);
        // Claims are ordered by DueAt, so the largest claim lag is the oldest pending row's lag at claim time; a full batch
        // (the backlog case) never reaches the peek.
        if (claims.Count > 0) ReportLag(claims.Max(c => c.LagSeconds));
        IReadOnlyList<DueItemResult> results = claims.Count == 0 ? [] : await RunClaimsAsync(claims, ct);

        if (claims.Count >= DueActionRules.BatchSize) return new DuePassResult(claims.Count, TimeSpan.Zero, results);

        // Reset before the peek: a row committed after the peek wakes us, so its wake must survive until the wait.
        signal.Reset();
        var next = await store.PeekNextDueSecondsAsync(ct);
        if (next is { } s) ReportLag(-s);
        return new DuePassResult(claims.Count, DueActionRules.SleepFor(next), results);
    }

    private async Task<IReadOnlyList<DueItemResult>> RunClaimsAsync(IReadOnlyList<DueClaim> claims, CancellationToken ct)
    {
        var results = new DueItemResult[claims.Count];
        using var gate = new SemaphoreSlim(DueActionRules.Parallelism);
        var tasks = new Task[claims.Count];
        for (var i = 0; i < claims.Count; i++)
        {
            var index = i;
            tasks[i] = Task.Run(async () =>
            {
                try
                {
                    await gate.WaitAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    results[index] = DueItemResult.Released;
                    return;
                }
                try
                {
                    results[index] = await RunItemAsync(claims[index], ct);
                }
                finally
                {
                    gate.Release();
                }
            }, CancellationToken.None);
        }
        await Task.WhenAll(tasks);

        var released = claims.Where((_, i) => results[i] == DueItemResult.Released).ToList();
        if (released.Count > 0)
        {
            try
            {
                await store.ReleaseAsync(released, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Releasing {Count} due actions failed; their lease will expire", released.Count);
            }
        }
        return results;
    }

    public async Task<DueItemResult> RunItemAsync(DueClaim claim, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return DueItemResult.Released;
        var item = claim.Item;
        try
        {
            await using var scope = await scopes.BeginAsync(ct);
            if (!await scope.IsStillClaimedAsync(claim, ct)) return DueItemResult.Stale;

            var handler = scope.HandlerFor(item.Kind);
            if (handler is not null)
            {
                var outcome = await handler.HandleAsync(scope.Db, item, ct);
                if (!await scope.CompleteAsync(claim, outcome, ct)) return DueItemResult.Stale;
                await scope.CommitAsync(ct);
                await AfterCommitAsync(scope, handler, item, outcome, ct);
                return outcome == DueOutcome.Done ? DueItemResult.Done : DueItemResult.Skipped;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return DueItemResult.Released;
        }
        catch (Exception ex)
        {
            return await FailAsync(claim, ex);
        }

        // Unknown kind: the item scope is already disposed (rolled back), so the park is its own statement.
        return await ParkUnknownAsync(claim);
    }

    private async Task AfterCommitAsync(IDueActionItemScope scope, IDueActionHandler handler, DueActionItem item, DueOutcome outcome, CancellationToken ct)
    {
        try
        {
            scope.SignalCommitted();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Waking the outbox after due action {Id} ({Kind}) failed", item.Id, item.Kind);
        }
        if (handler is not IDueActionAfterCommit after) return;
        try
        {
            await after.AfterCommitAsync(item, outcome, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "After commit step of due action {Id} ({Kind}) failed", item.Id, item.Kind);
        }
    }

    private async Task<DueItemResult> FailAsync(DueClaim claim, Exception ex)
    {
        var item = claim.Item;
        try
        {
            var result = await store.FailAsync(claim, DueActionRules.TrimError($"{ex.GetType().Name}: {ex.Message}"), CancellationToken.None);
            if (!result.Applied) return DueItemResult.Stale;
            if (result.Parked)
            {
                logger.LogError(TaxiRequestLogEvents.DueParked, ex, "Due action {Id} ({Kind}) parked after {Limit} attempts", item.Id, item.Kind, DueActionRules.MaxAttempts);
                return DueItemResult.Parked;
            }
            logger.LogWarning(ex, "Due action {Id} ({Kind}) failed, attempt {Attempt}", item.Id, item.Kind, item.Attempts + 1);
            return DueItemResult.Failed;
        }
        catch (Exception storeEx)
        {
            logger.LogError(storeEx, "Recording the failure of due action {Id} failed; its lease will expire", item.Id);
            return DueItemResult.Failed;
        }
    }

    private async Task<DueItemResult> ParkUnknownAsync(DueClaim claim)
    {
        var item = claim.Item;
        try
        {
            if (!await store.ParkAsync(claim, $"No handler for kind {item.Kind}", CancellationToken.None)) return DueItemResult.Stale;
            logger.LogError(TaxiRequestLogEvents.DueParked, "Due action {Id} parked: no handler for kind {Kind}", item.Id, item.Kind);
            return DueItemResult.Parked;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Parking due action {Id} failed; its lease will expire", item.Id);
            return DueItemResult.Failed;
        }
    }

    private void ReportLag(double lateSeconds)
    {
        if (double.IsNaN(lateSeconds) || lateSeconds <= DueActionRules.LagAlertSeconds) return;
        var now = _utcNow();
        if (now < _nextLagLog) return;
        _nextLagLog = now + LagLogEvery;
        logger.LogWarning(TaxiRequestLogEvents.DueLag, "Oldest pending due action is {LagSeconds:F0} s late", lateSeconds);
    }
}

public sealed class DueActionItemScopeFactory(IServiceScopeFactory scopeFactory) : IDueActionItemScopeFactory
{
    public async Task<IDueActionItemScope> BeginAsync(CancellationToken ct)
    {
        var scope = scopeFactory.CreateAsyncScope();
        try
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            return new DueActionItemScope(scope, db, tx);
        }
        catch
        {
            await scope.DisposeAsync();
            throw;
        }
    }

    private sealed class DueActionItemScope(AsyncServiceScope scope, ApplicationDbContext db, IDbContextTransaction tx) : IDueActionItemScope
    {
        private bool _committed;

        public ApplicationDbContext Db => db;

        public IDueActionHandler? HandlerFor(string kind)
            => scope.ServiceProvider.GetServices<IDueActionHandler>().FirstOrDefault(h => h.Kind == kind);

        public async Task<bool> IsStillClaimedAsync(DueClaim claim, CancellationToken ct)
            => (await db.Database.SqlQueryRaw<int>(DueActionSql.StillClaimed,
                DueActionSql.IdParameter(claim.Item.Id), DueActionSql.LeaseParameter(claim.LeaseUntil)).ToListAsync(ct)).Count > 0;

        public async Task<bool> CompleteAsync(DueClaim claim, DueOutcome outcome, CancellationToken ct)
        {
            var state = outcome == DueOutcome.Done ? DueActionStates.Done : DueActionStates.Skipped;
            var marked = await db.Database.ExecuteSqlRawAsync(DueActionSql.Complete,
            [
                new NpgsqlParameter("state", NpgsqlDbType.Varchar) { Value = state },
                DueActionSql.IdParameter(claim.Item.Id),
                DueActionSql.LeaseParameter(claim.LeaseUntil),
            ], ct);
            if (marked == 0) return false;
            await db.SaveChangesAsync(ct);
            return true;
        }

        public async Task CommitAsync(CancellationToken ct)
        {
            await tx.CommitAsync(ct);
            _committed = true;
        }

        public void SignalCommitted() => scope.ServiceProvider.GetService<IOutboxSignal>()?.Wake();

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!_committed) await tx.RollbackAsync(CancellationToken.None);
            }
            catch
            {
                // The connection is gone; the server already rolled the transaction back.
            }
            await tx.DisposeAsync();
            await scope.DisposeAsync();
        }
    }
}
