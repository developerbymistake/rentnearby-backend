using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.TaxiRequests.DueActions;

namespace RentNearBy.Infrastructure.Services.TaxiRequests.DueHandlers;

// MASTER_PLAN 2.7 ConfirmExpire: the selected driver did not confirm in 15 minutes. Selected -> Missed (ConfirmTimeout),
// the request goes back to Collecting or Open and the traveller is told. A confirm or decline meanwhile is skipped.
public sealed class ConfirmExpireHandler(TaxiRequestStore store, TaxiOfferService offers) : IDueActionHandler, IDueActionAfterCommit
{
    private TaxiClosureResult? _signals;

    public string Kind => DueActionKinds.ConfirmExpire;

    public async Task<DueOutcome> HandleAsync(ApplicationDbContext db, DueActionItem item, CancellationToken ct)
    {
        var first = await TaxiOfferRows.FindAsync(db, item.SubjectId, ct);
        if (first is null || first.Status != OfferStates.Selected) return DueOutcome.Skipped;
        if (await store.LockRequestAsync(db, first.RequestId, ct) is null) return DueOutcome.Skipped;
        var offer = await TaxiOfferRows.FindAsync(db, first.Id, ct);
        if (offer is null || offer.Status != OfferStates.Selected) return DueOutcome.Skipped;

        _signals = await offers.LapseSelectionInTxAsync(db, offer.RequestId, offer, OfferStates.Missed, OfferEndReasons.ConfirmTimeout, null, ct);
        return _signals is null ? DueOutcome.Skipped : DueOutcome.Done;
    }

    public Task AfterCommitAsync(DueActionItem item, DueOutcome outcome, CancellationToken ct)
        => _signals is { } signals ? store.AfterCommitAsync(signals) : Task.CompletedTask;
}
