using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities.TaxiRequests;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.TaxiRequests.DueActions;

namespace RentNearBy.Infrastructure.Services.TaxiRequests.DueHandlers;

// MASTER_PLAN 2.7 BookingAutoComplete: 00:05 IST after the last needed day the booking and request complete (Auto), the
// ride is counted and the traveller is asked for a rating. A booking ended or cancelled meanwhile is skipped.
public sealed class BookingAutoCompleteHandler(TaxiRequestStore store, TaxiBookingService bookings) : IDueActionHandler, IDueActionAfterCommit
{
    private TaxiClosureResult? _signals;

    public string Kind => DueActionKinds.BookingAutoComplete;

    public async Task<DueOutcome> HandleAsync(ApplicationDbContext db, DueActionItem item, CancellationToken ct)
    {
        var requestId = await db.Set<TaxiBooking>().AsNoTracking().Where(b => b.Id == item.SubjectId)
            .Select(b => (Guid?)b.RequestId).FirstOrDefaultAsync(ct);
        if (requestId is null || await store.LockRequestAsync(db, requestId.Value, ct) is null) return DueOutcome.Skipped;

        _signals = await bookings.CompleteInTxAsync(db, requestId.Value, item.SubjectId, BookingCompletedBy.Auto, ct);
        return _signals is null ? DueOutcome.Skipped : DueOutcome.Done;
    }

    public Task AfterCommitAsync(DueActionItem item, DueOutcome outcome, CancellationToken ct)
        => _signals is { } signals ? store.AfterCommitAsync(signals) : Task.CompletedTask;
}
