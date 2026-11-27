using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Interfaces.Buddy;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services.Owner.Quote;

// Per request: what the pre-deletion Play sync learned. Gone = Google no longer knows the token (never blocks);
// SyncUnsure = Google could not be asked (the DB state stands and the message adds the "just cancelled?" line).
public sealed class SubscriptionDeletionContext
{
    public HashSet<Guid> GoneSubscriptionIds { get; } = [];
    public bool SyncUnsure { get; set; }

    public void CopyFrom(SubscriptionDeletionContext? other)
    {
        if (other == null) return;
        GoneSubscriptionIds.UnionWith(other.GoneSubscriptionIds);
        SyncUnsure |= other.SyncUnsure;
    }
}

// ARCHITECTURE 11 (all roles, Q20): an account with an AUTO-RENEWING Google Play subscription cannot be deleted; the block carries the
// one clear step (Manage in Google Play). Time-bounded (S4): a row stuck with an old expiry never blocks, so it is never a dead end.
// Auto-renew off (cancelled inside the paid period, or ended) = no block; the existing anonymise path of the deletion runs.
public sealed class SubscriptionDeletionGuard(
    ApplicationDbContext db, SubscriptionDeletionContext context, IOptions<PlayBillingOptions> playOptions, TimeProvider clock)
    : IAccountDeletionParticipant
{
    public static readonly TimeSpan OnHoldWindow = TimeSpan.FromDays(60);

    // Lowest order: its block is the first reason the app shows.
    public int Order => 0;

    public static Expression<Func<PlaySubscription, bool>> Blocking(Guid userId, DateTime nowUtc)
    {
        var onHoldFrom = nowUtc - OnHoldWindow;
        return s => s.UserId == userId && s.MappingStatus == PlayMappingStatuses.Mapped && s.AutoRenewEnabled
            && (((s.State == PlayStates.Active || s.State == PlayStates.InGracePeriod) && s.ExpiryTime > nowUtc)
                || (s.State == PlayStates.OnHold && s.ExpiryTime > onHoldFrom)
                || (s.State == PlayStates.Paused && (s.PausedAutoResumeAt == null || s.PausedAutoResumeAt > nowUtc)));
    }

    public async Task<IReadOnlyList<AccountDeletionBlock>> CheckAsync(Guid userId, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var rows = await db.PlaySubscriptions.AsNoTracking()
            .Where(Blocking(userId, now))
            .OrderByDescending(s => s.ExpiryTime)
            .Select(s => new { s.Id, s.ProductId })
            .ToListAsync(ct);
        var first = rows.FirstOrDefault(r => !context.GoneSubscriptionIds.Contains(r.Id));
        if (first == null) return [];
        return [Block(PlayManageUrls.For(first.ProductId, playOptions.Value.PackageName), context.SyncUnsure)];
    }

    public static AccountDeletionBlock Block(string manageUrl, bool syncUnsure) => new(
        AccountDeletionReasons.ActiveSubscription,
        AccountDeletionReasons.ActiveSubscriptionMessage + (syncUnsure ? AccountDeletionReasons.JustCancelledHint : string.Empty),
        AccountDeletionActions.ManagePlay,
        ReasonKey: AccountDeletionRules.ReasonKeyOf(AccountDeletionReasons.ActiveSubscription),
        ArgsJson: JsonSerializer.Serialize(new { manageUrl }, ArgsJson),
        Title: AccountDeletionReasons.ActiveSubscriptionTitle);

    private static readonly JsonSerializerOptions ArgsJson = new(JsonSerializerDefaults.Web);

    public Task OnDeletingAsync(Guid userId, CancellationToken ct = default) => Task.CompletedTask;

    public Task AfterCommitAsync(Guid userId, CancellationToken ct = default) => Task.CompletedTask;
}

// Freshness before the deletion transaction (ARCHITECTURE 11.3): only when the DB shows a blocking row, each such token is synced
// with Google OUTSIDE any transaction, 5 s in total at most. Every sync runs in its OWN DI scope (own ApplicationDbContext): the sync
// takes no cancellation token, so a sync that outlives the wait keeps running, and it must never touch the request's context that the
// deletion transaction uses next. Its scope is disposed only after the sync task has ended. Updated row = the locked check decides;
// NotFound / Invalid = token gone, never blocks; any other API error, a timeout, Play not configured (the sync throws) or any other
// exception = the DB state stands with the "just cancelled?" line.
public sealed class SubscriptionDeletionPreCheck(
    ApplicationDbContext db, SubscriptionDeletionContext context, IServiceScopeFactory scopes, TimeProvider clock,
    ILogger<SubscriptionDeletionPreCheck> logger)
{
    public static readonly TimeSpan SyncTimeout = TimeSpan.FromSeconds(5);

    // The whole pre-check budget (all tokens together). Tests shorten it; production keeps SyncTimeout.
    public TimeSpan Budget { get; init; } = SyncTimeout;

    public async Task RefreshAsync(Guid userId, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var rows = await db.PlaySubscriptions.AsNoTracking()
            .Where(SubscriptionDeletionGuard.Blocking(userId, now))
            .Select(s => new { s.Id, s.PurchaseToken })
            .ToListAsync(ct);
        if (rows.Count == 0) return;
        await SyncTokensAsync(rows.Select(r => (r.Id, r.PurchaseToken)).ToList(), ct);
        // The sync wrote through the context of its own scope; this one must not hold stale copies for the deletion transaction.
        db.ChangeTracker.Clear();
    }

    // One Google call per blocking token (normally one), each in a fresh scope, all inside one Budget.
    public async Task SyncTokensAsync(IReadOnlyList<(Guid Id, string Token)> rows, CancellationToken ct = default)
    {
        var deadline = clock.GetUtcNow() + Budget;
        foreach (var (id, token) in rows) // batch-loop: bounded by the shared deadline
        {
            var left = deadline - clock.GetUtcNow();
            if (left <= TimeSpan.Zero)
            {
                context.SyncUnsure = true;
                return;
            }
            var scope = scopes.CreateAsyncScope();
            Task<PlaySyncResult>? task = null;
            try
            {
                var sync = scope.ServiceProvider.GetService<IPlayBillingSyncService>();
                if (sync == null)
                {
                    context.SyncUnsure = true;
                    return;
                }
                task = sync.SyncTokenAsync(new PlaySyncRequest(token, PurchaseSources.PlayReconcile));
                var result = await task.WaitAsync(left, clock, ct);
                if (IsGone(result))
                {
                    context.GoneSubscriptionIds.Add(id);
                    logger.LogInformation("Deletion pre-check: Play token gone for subscription {SubscriptionId}", id);
                }
                else if (!IsStateUpdate(result))
                {
                    context.SyncUnsure = true;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                context.SyncUnsure = true;
                logger.LogWarning("Deletion pre-check: Play sync failed for subscription {SubscriptionId}: {Error}", id, ex.GetType().Name);
                if (ex is TimeoutException) return; // the budget is spent; a sync still running owns its scope until it ends
            }
            finally
            {
                await ReleaseScopeAsync(scope, task);
            }
        }
    }

    // A finished (or never started) sync releases its scope now; a running one releases it from its own continuation, so its
    // context is never disposed under it. Its late fault is observed and logged, never thrown at nobody.
    private async Task ReleaseScopeAsync(AsyncServiceScope scope, Task<PlaySyncResult>? task)
    {
        if (task == null || task.IsCompleted)
        {
            await scope.DisposeAsync();
            return;
        }
        _ = task.ContinueWith(async t =>
        {
            if (t.IsFaulted) logger.LogWarning("Deletion pre-check: late Play sync failed: {Error}", t.Exception?.GetBaseException().GetType().Name);
            await scope.DisposeAsync();
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
    }

    public static bool IsGone(PlaySyncResult result)
        => result.Outcome == PlaySyncOutcome.Invalid
           || (result.Outcome == PlaySyncOutcome.ApiError && result.ApiError is PlayApiErrorKind.NotFound or PlayApiErrorKind.Invalid);

    // An API error wrote no fresh Google state into the row (the gone kinds are handled first): the DB state stands with the hint.
    public static bool IsStateUpdate(PlaySyncResult result) => result.Outcome != PlaySyncOutcome.ApiError;
}
