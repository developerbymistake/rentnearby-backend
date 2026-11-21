using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services.Owner;

// Contract 8.1 "one hook, not 35": every SaveChanges that adds a Stay / Taxi enquiry, or changes its Status, CheckIn, CheckOut,
// AcceptedOfferId or AgreedTotal, runs IOwnerCalendarSync for those enquiries in the same transaction. When no transaction is open
// the interceptor opens one before the save and commits it after the sync, so the booking change and its Auto entry commit together.
// Singleton on the pooled context options: per-context state lives in a ConditionalWeakTable and is removed after every save.
public sealed class OwnerCalendarSyncInterceptor(IOwnerCalendarSync sync) : SaveChangesInterceptor
{
    private static readonly string[] WatchedProperties =
    [
        nameof(TripEnquiry.Status), nameof(TripEnquiry.CheckIn), nameof(TripEnquiry.CheckOut),
        nameof(TripEnquiry.AcceptedOfferId), nameof(TripEnquiry.AgreedTotal),
    ];

    private sealed class State
    {
        public readonly List<TripEnquiry> Enquiries = [];
        public IDbContextTransaction? Opened;
        public bool Running;
    }

    private readonly ConditionalWeakTable<DbContext, State> _states = new();

    public bool IsActive => sync is not NullOwnerCalendarSync;

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (Collect(eventData.Context) is { } state && eventData.Context!.Database.CurrentTransaction == null)
            state.Opened = await eventData.Context.Database.BeginTransactionAsync(cancellationToken);
        return result;
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (Collect(eventData.Context) is { } state && eventData.Context!.Database.CurrentTransaction == null)
            state.Opened = eventData.Context.Database.BeginTransaction();
        return result;
    }

    public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        await RunAsync(eventData.Context, cancellationToken);
        return result;
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        RunAsync(eventData.Context, CancellationToken.None).GetAwaiter().GetResult();
        return result;
    }

    public override async Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
        => await AbandonAsync(eventData.Context);

    public override void SaveChangesFailed(DbContextErrorEventData eventData) => AbandonAsync(eventData.Context).GetAwaiter().GetResult();

    public override async Task SaveChangesCanceledAsync(DbContextEventData eventData, CancellationToken cancellationToken = default)
        => await AbandonAsync(eventData.Context);

    public override void SaveChangesCanceled(DbContextEventData eventData) => AbandonAsync(eventData.Context).GetAwaiter().GetResult();

    // Pure part, unit tested: the enquiries of this save the calendar follows.
    // A new enquiry that is not confirmed has no Auto entry yet, so it needs no sync (status null = not known, watched).
    public static bool IsWatched(EntityState state, string kind, Func<string, bool> isModified, string? status = null)
    {
        if (kind is not (TripEnquiryKinds.Stay or TripEnquiryKinds.Taxi)) return false;
        if (state == EntityState.Added) return status is null or TripEnquiryStatuses.ConfirmedByBoth;
        return state == EntityState.Modified && WatchedProperties.Any(isModified);
    }

    private State? Collect(DbContext? context)
    {
        if (!IsActive || context is not ApplicationDbContext) return null;
        if (_states.TryGetValue(context, out var existing) && existing.Running) return null;

        List<TripEnquiry>? found = null;
        foreach (var entry in context.ChangeTracker.Entries<TripEnquiry>())
        {
            if (!IsWatched(entry.State, entry.Entity.Kind, p => entry.Property(p).IsModified, entry.Entity.Status)) continue;
            (found ??= []).Add(entry.Entity);
        }
        if (found == null) return null;

        var state = _states.GetValue(context, _ => new State());
        state.Enquiries.Clear();
        state.Enquiries.AddRange(found);
        return state;
    }

    private async Task RunAsync(DbContext? context, CancellationToken ct)
    {
        if (context is not ApplicationDbContext db || !_states.TryGetValue(context, out var state) || state.Running) return;
        state.Running = true;
        try
        {
            // One sync per enquiry of this save; a save rarely holds more than one.
            foreach (var id in state.Enquiries.Select(e => e.Id).Distinct().ToList()) // batch-loop
                await sync.SyncEnquiryAsync(db, id, ct);
            if (state.Opened != null) await state.Opened.CommitAsync(ct);
        }
        catch
        {
            if (state.Opened != null) await state.Opened.RollbackAsync(CancellationToken.None);
            throw;
        }
        finally
        {
            await CleanupAsync(context, state);
        }
    }

    private async Task AbandonAsync(DbContext? context)
    {
        if (context == null || !_states.TryGetValue(context, out var state) || state.Running) return;
        try
        {
            if (state.Opened != null) await state.Opened.RollbackAsync(CancellationToken.None);
        }
        finally
        {
            await CleanupAsync(context, state);
        }
    }

    private async Task CleanupAsync(DbContext context, State state)
    {
        if (state.Opened != null) await state.Opened.DisposeAsync();
        state.Opened = null;
        state.Enquiries.Clear();
        state.Running = false;
        _states.Remove(context);
    }
}
