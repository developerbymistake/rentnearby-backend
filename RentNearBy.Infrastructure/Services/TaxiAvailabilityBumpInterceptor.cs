using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;
using StackExchange.Redis;

namespace RentNearBy.Infrastructure.Services;

// Contract 1.9, legacy leads: a ConfirmedByBoth taxi lead holds its days on the driver's calendar (Auto row), so every save that
// moves a taxi lead into or out of ConfirmedByBoth (confirm, cancel, withdraw, trip delete, timer, listing removal, purge) or
// changes the days of a confirmed one bumps taxis:avail:gen once the change is committed (the only bump for legacy leads, the
// driver confirm included). Rolled back work never bumps.
// Singleton on the pooled context options; per-context state lives in a ConditionalWeakTable. Register both this and Commits.
public sealed class TaxiAvailabilityBumpInterceptor : SaveChangesInterceptor
{
    private static readonly string[] DayProperties =
        [nameof(TripEnquiry.CheckIn), nameof(TripEnquiry.CheckOut), nameof(TripEnquiry.DaysJson)];

    private sealed class State
    {
        public bool Candidate;
        public bool Pending;
    }

    private readonly IConnectionMultiplexer? _redis;
    private readonly ConditionalWeakTable<DbContext, State> _states = new();

    public TaxiAvailabilityBumpInterceptor(IConnectionMultiplexer? redis)
    {
        _redis = redis;
        Commits = new CommitWatcher(this);
    }

    public DbTransactionInterceptor Commits { get; }

    // Pure part, unit tested. originalStatus is the status the row had before this save (null for a new row).
    public static bool MovesAvailability(EntityState state, string kind, string? status, string? originalStatus, Func<string, bool> isModified)
    {
        if (kind != TripEnquiryKinds.Taxi) return false;
        const string confirmed = TripEnquiryStatuses.ConfirmedByBoth;
        return state switch
        {
            EntityState.Added => status == confirmed,
            EntityState.Deleted => originalStatus == confirmed,
            EntityState.Modified => (status == confirmed) != (originalStatus == confirmed)
                || (status == confirmed && DayProperties.Any(isModified)),
            _ => false,
        };
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Collect(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Collect(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        if (Saved(eventData.Context)) BumpAsync().GetAwaiter().GetResult();
        return result;
    }

    public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        if (Saved(eventData.Context)) await BumpAsync();
        return result;
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData) => ClearCandidate(eventData.Context);

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        ClearCandidate(eventData.Context);
        return Task.CompletedTask;
    }

    public override void SaveChangesCanceled(DbContextEventData eventData) => ClearCandidate(eventData.Context);

    public override Task SaveChangesCanceledAsync(DbContextEventData eventData, CancellationToken cancellationToken = default)
    {
        ClearCandidate(eventData.Context);
        return Task.CompletedTask;
    }

    // SavingChanges runs before EF's own DetectChanges, so each tracked enquiry is detected here (that entity only, not the context).
    public static bool HasMove(ApplicationDbContext context)
    {
        var tracker = context.ChangeTracker;
        var autoDetect = tracker.AutoDetectChangesEnabled;
        tracker.AutoDetectChangesEnabled = false;
        try
        {
            foreach (var entry in tracker.Entries<TripEnquiry>())
            {
                entry.DetectChanges();
                var original = entry.State is EntityState.Modified or EntityState.Deleted
                    ? entry.Property(e => e.Status).OriginalValue
                    : null;
                if (MovesAvailability(entry.State, entry.Entity.Kind, entry.Entity.Status, original, p => entry.Property(p).IsModified)) return true;
            }
            return false;
        }
        finally
        {
            tracker.AutoDetectChangesEnabled = autoDetect;
        }
    }

    private void Collect(DbContext? context)
    {
        if (_redis == null || context is not ApplicationDbContext db) return;
        if (HasMove(db)) _states.GetValue(context, _ => new State()).Candidate = true;
        else if (_states.TryGetValue(context, out var state)) state.Candidate = false;
    }

    // True = the save is already committed (no transaction left open), bump now; inside an open transaction it waits for the commit.
    private bool Saved(DbContext? context)
    {
        if (context == null || !_states.TryGetValue(context, out var state) || !state.Candidate) return false;
        state.Candidate = false;
        if (context.Database.CurrentTransaction != null)
        {
            state.Pending = true;
            return false;
        }
        return true;
    }

    private void ClearCandidate(DbContext? context)
    {
        if (context != null && _states.TryGetValue(context, out var state)) state.Candidate = false;
    }

    private bool TakePending(DbContext? context)
    {
        if (context == null || !_states.TryGetValue(context, out var state) || !state.Pending) return false;
        state.Pending = false;
        return true;
    }

    private void DropPending(DbContext? context)
    {
        if (context != null && _states.TryGetValue(context, out var state)) state.Pending = false;
    }

    private Task BumpAsync() => FeedCache.BumpAsync(_redis, [ListingCacheKeys.TaxiAvailGen]);

    private sealed class CommitWatcher(TaxiAvailabilityBumpInterceptor owner) : DbTransactionInterceptor
    {
        // A new transaction never inherits a mark left by one that was disposed without a commit.
        public override DbTransaction TransactionStarted(DbConnection connection, TransactionEndEventData eventData, DbTransaction result)
        {
            owner.DropPending(eventData.Context);
            return result;
        }

        public override ValueTask<DbTransaction> TransactionStartedAsync(
            DbConnection connection, TransactionEndEventData eventData, DbTransaction result, CancellationToken cancellationToken = default)
        {
            owner.DropPending(eventData.Context);
            return ValueTask.FromResult(result);
        }

        public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData)
        {
            if (owner.TakePending(eventData.Context)) owner.BumpAsync().GetAwaiter().GetResult();
        }

        public override async Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (owner.TakePending(eventData.Context)) await owner.BumpAsync();
        }

        public override void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData) => owner.DropPending(eventData.Context);

        public override Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            owner.DropPending(eventData.Context);
            return Task.CompletedTask;
        }

        public override void TransactionFailed(DbTransaction transaction, TransactionErrorEventData eventData) => owner.DropPending(eventData.Context);

        public override Task TransactionFailedAsync(DbTransaction transaction, TransactionErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            owner.DropPending(eventData.Context);
            return Task.CompletedTask;
        }
    }
}
