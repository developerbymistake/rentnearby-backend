using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.TaxiRequests.DueActions;

namespace RentNearBy.Infrastructure.Services.TaxiRequests.DueHandlers;

// Missed rings rule (contract section 6, P26).
public static class RingMissRules
{
    // A ring that timed out counts when it was never acknowledged as shown in time: no ack, or an ack with late true or
    // shown false (both stored as PushLate). An acknowledged ring the driver simply left unanswered does not count.
    public static bool CountsAsMiss(DateTime? pushReceivedAt, bool pushLate) => pushReceivedAt is null || pushLate;

    // The notice goes out once: only on the miss that reaches the limit, never on a later value.
    public static bool NoticeDue(int? newStreak) => newStreak == TaxiDispatchRules.RingMissLimit;
}

// MASTER_PLAN 2.7 OfferExpire: Ringing -> Missed (Expired) after 300 s; counts a missed ring by the rule above; the driver
// is free again and the city gets another pass. A ring answered or closed meanwhile is skipped. A driver who reaches the
// limit leaves the pool, so the pickup lists are bumped after commit. Scoped: one per item.
public sealed class OfferExpireHandler(TaxiRequestStore store, ITaxiPushWriter pushes, TaxiPickupPlaces places) : IDueActionHandler, IDueActionAfterCommit
{
    private TaxiClosureResult? _signals;
    private bool _leftPool;

    public string Kind => DueActionKinds.OfferExpire;

    public async Task<DueOutcome> HandleAsync(ApplicationDbContext db, DueActionItem item, CancellationToken ct)
    {
        var first = await TaxiOfferRows.FindAsync(db, item.SubjectId, ct);
        if (first is null || first.Status != OfferStates.Ringing) return DueOutcome.Skipped;
        var request = await store.LockRequestAsync(db, first.RequestId, ct);
        if (request is null) return DueOutcome.Skipped;

        var moved = await store.TryMoveOfferAsync(db, first.Id, [OfferStates.Ringing], OfferStates.Missed, OfferEndReasons.Expired, ct: ct);
        if (moved is null) return DueOutcome.Skipped;

        // Read after the move: an ack that committed before it is seen; a later ack finds the offer no longer ringing.
        var offer = await TaxiOfferRows.FindAsync(db, first.Id, ct);
        _signals = await TaxiOfferRows.SignalsAsync(db, request.Id, request.DispatchCityKey, [first.DriverListingId], ct);
        if (offer is not null && RingMissRules.CountsAsMiss(offer.PushReceivedAt, offer.PushLate))
        {
            var streak = await store.AddRingMissAsync(db, first.DriverListingId, ct);
            _leftPool = RingMissRules.NoticeDue(streak);
            if (_leftPool)
                await pushes.NotifyAsync(db, new TaxiNotice(TaxiNoticeKind.RingsMissed, first.DriverUserId, request.TripId, request.Id, first.Id), ct);
        }
        return DueOutcome.Done;
    }

    public async Task AfterCommitAsync(DueActionItem item, DueOutcome outcome, CancellationToken ct)
    {
        if (_signals is { } signals) await store.AfterCommitAsync(signals);
        if (_leftPool) await places.BumpAsync();
    }
}
