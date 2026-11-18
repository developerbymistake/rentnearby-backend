using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using RentNearBy.Core.Entities.TaxiRequests;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services.TaxiRequests;

// Outcome of a driver or traveller action. Error = a TaxiRequestErrorTypes value (contract section 8); the caller re-reads
// the subject to build the response, so a repeat of a finished action answers with the current object.
public sealed record TaxiActionResult(string? Error, Guid? OfferId = null, Guid? BookingId = null, Guid? RequestId = null, object? Data = null)
{
    public bool Ok => Error is null;

    public static TaxiActionResult Fail(string error, object? data = null) => new(error, Data: data);

    public static TaxiActionResult StateOf(string error, string state) => new(error, Data: new { state });
}

public sealed record TaxiOfferFacts(Guid Id, Guid RequestId, Guid DriverListingId, Guid DriverUserId, string Status, string? EndReason,
    DateTime? PushReceivedAt, bool PushLate, bool RingOpen, bool ConfirmOpen);

// Offer rows read with the database clock. Always a fresh statement: callers read again after the request lock.
public static class TaxiOfferRows
{
    public const string FindSql = """
        SELECT o."Id", o."RequestId", o."DriverListingId", o."DriverUserId", o."Status", o."EndReason", o."PushReceivedAt", o."PushLate",
               (o."ExpiresAt" > now()) AS "RingOpen", (o."ConfirmBy" IS NOT NULL AND o."ConfirmBy" > now()) AS "ConfirmOpen"
        FROM "TaxiRequestOffers" o WHERE o."Id" = @id
        """;

    public const string CurrentRequestSql = """
        SELECT "Version", "Status", "TripId", "TravellerUserId", "DispatchCityKey" FROM "TaxiRequests" WHERE "Id" = @id
        """;

    // Same rule as DriverLockRow.IsBookable. A plain read, no lock: the caller holds only the request row (2.4).
    public static readonly string DriverBookableSql = $"""
        SELECT EXISTS (SELECT 1 FROM "TaxiListings" l WHERE l."Id" = @driver AND NOT l."IsDeleted" AND l."Status" = '{TaxiStatuses.Approved}') AS "Value"
        """;

    // A taken down, unapproved or deleted taxi can no longer accept a ring or be selected, whichever path changed it.
    public static async Task<bool> IsDriverBookableAsync(ApplicationDbContext db, Guid driverListingId, CancellationToken ct)
        => await db.Database.SqlQueryRaw<bool>(DriverBookableSql, Uuid("driver", driverListingId)).SingleAsync(ct);

    public static async Task<TaxiOfferFacts?> FindAsync(ApplicationDbContext db, Guid offerId, CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<OfferRow>(FindSql, Uuid("id", offerId)).ToListAsync(ct);
        if (rows.Count == 0) return null;
        var r = rows[0];
        return new TaxiOfferFacts(r.Id, r.RequestId, r.DriverListingId, r.DriverUserId, r.Status, r.EndReason, r.PushReceivedAt, r.PushLate, r.RingOpen, r.ConfirmOpen);
    }

    // The request as it stands after every bump of this transaction, for the realtime hint.
    public static async Task<TaxiRequestChange?> ChangeOfAsync(ApplicationDbContext db, Guid requestId, CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<CurrentRow>(CurrentRequestSql, Uuid("id", requestId)).ToListAsync(ct);
        return rows.Count == 0 ? null : new TaxiRequestChange(requestId, rows[0].TripId, rows[0].TravellerUserId, rows[0].Version, rows[0].Status);
    }

    public static async Task<TaxiClosureResult> SignalsAsync(ApplicationDbContext db, Guid requestId, long cityKey, IEnumerable<Guid> freedDrivers, CancellationToken ct)
    {
        var change = await ChangeOfAsync(db, requestId, ct);
        return new TaxiClosureResult([cityKey], freedDrivers.Distinct().ToList(), change is null ? [] : [change]);
    }

    internal static NpgsqlParameter Uuid(string name, Guid value) => new(name, NpgsqlDbType.Uuid) { Value = value };

    private sealed class OfferRow
    {
        public Guid Id { get; set; }
        public Guid RequestId { get; set; }
        public Guid DriverListingId { get; set; }
        public Guid DriverUserId { get; set; }
        public string Status { get; set; } = "";
        public string? EndReason { get; set; }
        public DateTime? PushReceivedAt { get; set; }
        public bool PushLate { get; set; }
        public bool RingOpen { get; set; }
        public bool ConfirmOpen { get; set; }
    }

    private sealed class CurrentRow
    {
        public long Version { get; set; }
        public string Status { get; set; } = "";
        public Guid TripId { get; set; }
        public Guid TravellerUserId { get; set; }
        public long DispatchCityKey { get; set; }
    }
}

// MASTER_PLAN B2-6, driver side of an offer: accept (3 accept cap), decline, withdraw. Every request-level change goes
// through TaxiRequestStore; lock order 2.4: request row first, then offers, bookings, due actions, outbox, counters last.
public sealed class TaxiOfferService(
    ApplicationDbContext db,
    TaxiRequestStore store,
    ITaxiPushWriter pushes,
    IDueActionScheduler scheduler,
    ILogger<TaxiOfferService> logger,
    TaxiRequestMetrics? metrics = null)
{
    private static readonly string[] Ringing = [OfferStates.Ringing];
    private static readonly string[] SelectedOnly = [OfferStates.Selected];

    public static readonly string AcceptCountSql = $"""
        UPDATE "TaxiRequests" SET "AcceptedCount" = "AcceptedCount" + 1,
            "Status" = CASE WHEN "Status" = '{RequestStates.Open}' THEN '{RequestStates.Collecting}' ELSE "Status" END,
            "Version" = "Version" + 1, "UpdatedAt" = now()
        WHERE "Id" = @id AND "Status" IN ('{RequestStates.Open}', '{RequestStates.Collecting}', '{RequestStates.Selected}')
          AND "AcceptedCount" < "AcceptTarget"
        RETURNING "AcceptedCount"::int AS "AcceptedCount", "AcceptTarget"::int AS "AcceptTarget"
        """;

    public static readonly string AcceptOfferSql = $"""
        UPDATE "TaxiRequestOffers" SET "Status" = '{OfferStates.Accepted}', "RespondedAt" = now()
        WHERE "Id" = @id AND "Status" = '{OfferStates.Ringing}' AND "ExpiresAt" > now()
        """;

    // Accept, decline and withdraw are answers by the driver himself.
    public static bool IsAcceptAnswered(string offerState) => offerState is OfferStates.Accepted or OfferStates.Selected or OfferStates.Confirmed;

    public static string AcceptRefusal(string offerState)
        => offerState is OfferStates.Missed or OfferStates.Closed ? TaxiRequestErrorTypes.OfferMissed : TaxiRequestErrorTypes.OfferStateInvalid;

    public Task<TaxiActionResult> AcceptAsync(Guid offerId, Guid driverUserId, CancellationToken ct = default)
        => PgRetry.OnceAsync(db, c => AcceptUnitAsync(offerId, driverUserId, c), ct, logger);

    public Task<TaxiActionResult> DeclineAsync(Guid offerId, Guid driverUserId, string? chip, CancellationToken ct = default)
        => PgRetry.OnceAsync(db, c => DeclineUnitAsync(offerId, driverUserId, chip, c), ct, logger);

    public Task<TaxiActionResult> WithdrawAsync(Guid offerId, Guid driverUserId, string? chip, CancellationToken ct = default)
        => PgRetry.OnceAsync(db, c => WithdrawUnitAsync(offerId, driverUserId, chip, c), ct, logger);

    private async Task<TaxiActionResult> AcceptUnitAsync(Guid offerId, Guid driverUserId, CancellationToken ct)
    {
        if (await OwnedAsync(offerId, driverUserId, ct) is { } denied) return denied;
        TaxiClosureResult signals;
        TaxiActionResult result;
        await using (var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct))
        {
            var (request, offer) = await LockAsync(offerId, ct);
            if (request is null || offer is null) return TaxiActionResult.Fail(TaxiRequestErrorTypes.OfferNotFound);
            if (offer.Status != OfferStates.Ringing)
                return IsAcceptAnswered(offer.Status) ? Done(offer) : TaxiActionResult.StateOf(AcceptRefusal(offer.Status), offer.Status);

            // The taxi left review or was removed, or the driver got booked on these dates after the ring went out:
            // the ring closes instead of counting as an accept. Plain reads, the listing row is never locked here (2.4).
            var (closeReason, refusal) = await AcceptBlockAsync(request, offer.DriverListingId, ct);
            if (closeReason is not null)
            {
                if (await store.TryMoveOfferAsync(db, offerId, Ringing, OfferStates.Closed, closeReason, ct: ct) is null)
                    return TaxiActionResult.StateOf(TaxiRequestErrorTypes.OfferMissed, OfferStates.Missed);
                await scheduler.CancelAsync(db, DueActionKinds.OfferExpire, offerId, ct);
                signals = await TaxiOfferRows.SignalsAsync(db, request.Id, request.DispatchCityKey, [offer.DriverListingId], ct);
                await db.SaveChangesAsync(ct);
                await store.ResetRingMissAsync(db, offer.DriverListingId, ct);
                await tx.CommitAsync(ct);
                metrics?.OfferEnded(OfferStates.Closed);
                await store.AfterCommitAsync(signals);
                return refusal!;
            }

            var counted = await db.Database.SqlQueryRaw<CountRow>(AcceptCountSql, TaxiOfferRows.Uuid("id", request.Id)).ToListAsync(ct);
            if (counted.Count == 0)
            {
                // Request full or no longer collecting: the ring ends as Missed (Filled). The driver answered, so the ring arrived.
                if (await store.TryMoveOfferAsync(db, offerId, Ringing, OfferStates.Missed, OfferEndReasons.Filled, ct: ct) is null)
                    return TaxiActionResult.StateOf(TaxiRequestErrorTypes.OfferMissed, OfferStates.Missed);
                await scheduler.CancelAsync(db, DueActionKinds.OfferExpire, offerId, ct);
                signals = await TaxiOfferRows.SignalsAsync(db, request.Id, request.DispatchCityKey, [offer.DriverListingId], ct);
                await db.SaveChangesAsync(ct);
                await store.ResetRingMissAsync(db, offer.DriverListingId, ct);
                await tx.CommitAsync(ct);
                metrics?.OfferEnded(OfferStates.Missed);
                result = TaxiActionResult.StateOf(TaxiRequestErrorTypes.OfferMissed, OfferStates.Missed);
            }
            else
            {
                if (await db.Database.ExecuteSqlRawAsync(AcceptOfferSql, [TaxiOfferRows.Uuid("id", offerId)], ct) == 0)
                {
                    await tx.RollbackAsync(ct);
                    return TaxiActionResult.StateOf(TaxiRequestErrorTypes.OfferMissed, OfferStates.Missed);
                }

                var freed = new List<Guid> { offer.DriverListingId };
                if (counted[0].AcceptedCount >= counted[0].AcceptTarget)
                {
                    var closed = await store.CloseOffersAsync(db, request.Id, Ringing, OfferStates.Closed, OfferEndReasons.Filled, offerId, ct);
                    foreach (var o in closed)
                    {
                        await scheduler.CancelAsync(db, DueActionKinds.OfferExpire, o.OfferId, ct);
                        await pushes.RingEndAsync(db, new RingEndPush(o.OfferId, request.Id, o.DriverUserId, OfferEndReasons.Filled), ct);
                        freed.Add(o.DriverListingId);
                    }
                }
                await scheduler.CancelAsync(db, DueActionKinds.OfferExpire, offerId, ct);
                await pushes.NotifyAsync(db, new TaxiNotice(TaxiNoticeKind.DriverAccepted, request.TravellerUserId, request.TripId, request.Id, offerId), ct);
                signals = await TaxiOfferRows.SignalsAsync(db, request.Id, request.DispatchCityKey, freed, ct);
                await db.SaveChangesAsync(ct);
                await store.ResetRingMissAsync(db, offer.DriverListingId, ct);
                await tx.CommitAsync(ct);
                metrics?.OfferEnded(OfferStates.Accepted);
                result = Done(offer);
            }
        }
        await store.AfterCommitAsync(signals);
        return result;
    }

    // Contract D3 knows one refusal for a ring that can no longer count: OfferMissed, with the offer's real state.
    public static TaxiActionResult ClosedRingRefusal => TaxiActionResult.StateOf(AcceptRefusal(OfferStates.Closed), OfferStates.Closed);

    private async Task<(string? CloseReason, TaxiActionResult? Refusal)> AcceptBlockAsync(TaxiRequest request, Guid driverListingId, CancellationToken ct)
    {
        if (!await TaxiOfferRows.IsDriverBookableAsync(db, driverListingId, ct))
            return (OfferEndReasons.ListingRemoved, ClosedRingRefusal);
        if (await DriverCalendar.ConflictForAsync(db, driverListingId, request.DateFrom, request.DateTo, ct) is not null)
            return (OfferEndReasons.Booked, ClosedRingRefusal);
        return (null, null);
    }

    private async Task<TaxiActionResult> DeclineUnitAsync(Guid offerId, Guid driverUserId, string? chip, CancellationToken ct)
    {
        if (chip is not null && !ReasonChips.IsValid(chip)) return TaxiActionResult.Fail(TaxiRequestErrorTypes.ReasonRequired);
        if (await OwnedAsync(offerId, driverUserId, ct) is { } denied) return denied;
        TaxiClosureResult signals;
        await using (var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct))
        {
            var (request, offer) = await LockAsync(offerId, ct);
            if (request is null || offer is null) return TaxiActionResult.Fail(TaxiRequestErrorTypes.OfferNotFound);
            switch (offer.Status)
            {
                case OfferStates.Declined:
                    return Done(offer);
                case OfferStates.Ringing:
                    if (await store.TryMoveOfferAsync(db, offerId, Ringing, OfferStates.Declined, null, chip, ct) is null)
                        return TaxiActionResult.StateOf(TaxiRequestErrorTypes.OfferStateInvalid, offer.Status);
                    await scheduler.CancelAsync(db, DueActionKinds.OfferExpire, offerId, ct);
                    signals = await TaxiOfferRows.SignalsAsync(db, request.Id, request.DispatchCityKey, [offer.DriverListingId], ct);
                    await db.SaveChangesAsync(ct);
                    await store.ResetRingMissAsync(db, offer.DriverListingId, ct);
                    break;
                case OfferStates.Selected:
                    var lapsed = await LapseSelectionInTxAsync(db, request.Id, offer, OfferStates.Declined, OfferEndReasons.DeclinedBooking, chip, ct);
                    if (lapsed is null) return TaxiActionResult.StateOf(TaxiRequestErrorTypes.OfferStateInvalid, offer.Status);
                    signals = lapsed;
                    break;
                default:
                    return TaxiActionResult.StateOf(TaxiRequestErrorTypes.OfferStateInvalid, offer.Status);
            }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        metrics?.OfferEnded(OfferStates.Declined);
        await store.AfterCommitAsync(signals);
        return new TaxiActionResult(null, OfferId: offerId);
    }

    private async Task<TaxiActionResult> WithdrawUnitAsync(Guid offerId, Guid driverUserId, string? chip, CancellationToken ct)
    {
        if (!ReasonChips.IsValid(chip)) return TaxiActionResult.Fail(TaxiRequestErrorTypes.ReasonRequired);
        if (await OwnedAsync(offerId, driverUserId, ct) is { } denied) return denied;
        TaxiClosureResult signals;
        await using (var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct))
        {
            var (request, offer) = await LockAsync(offerId, ct);
            if (request is null || offer is null) return TaxiActionResult.Fail(TaxiRequestErrorTypes.OfferNotFound);
            switch (offer.Status)
            {
                case OfferStates.Withdrawn:
                    return Done(offer);
                case OfferStates.Accepted:
                    signals = await WithdrawAcceptedAsync(request.Id, request.DispatchCityKey, offer, chip!, ct);
                    break;
                case OfferStates.Confirmed:
                    var confirmed = await WithdrawConfirmedAsync(request.Id, request.DispatchCityKey, request.DateFrom, offer, chip!, ct);
                    if (confirmed is null) return TaxiActionResult.StateOf(TaxiRequestErrorTypes.OfferStateInvalid, offer.Status);
                    signals = confirmed;
                    break;
                default:
                    return TaxiActionResult.StateOf(TaxiRequestErrorTypes.OfferStateInvalid, offer.Status);
            }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        metrics?.OfferEnded(OfferStates.Withdrawn);
        await store.AfterCommitAsync(signals);
        return new TaxiActionResult(null, OfferId: offerId);
    }

    // Leaves before being chosen. The request goes back to Open when no live accepted driver is left (2.3).
    private async Task<TaxiClosureResult> WithdrawAcceptedAsync(Guid requestId, long cityKey, TaxiOfferFacts offer, string chip, CancellationToken ct)
    {
        await store.TryMoveOfferAsync(db, offer.Id, [OfferStates.Accepted], OfferStates.Withdrawn, null, chip, ct);
        var counts = await store.GetLiveCountsAsync(db, requestId, ct);
        var current = await TaxiOfferRows.ChangeOfAsync(db, requestId, ct);
        if (counts.Accepted == 0 && current?.State == RequestStates.Collecting)
            await store.ReopenRequestAsync(db, requestId, 0, ct);
        return await TaxiOfferRows.SignalsAsync(db, requestId, cityKey, [offer.DriverListingId], ct);
    }

    // P10: before the first trip day (booking still Booked) the booking is cancelled and the request reopens with 3 more
    // accepts; during the trip (InProgress) the request ends as DriverLeftTrip. null = no active booking for this offer.
    private async Task<TaxiClosureResult?> WithdrawConfirmedAsync(Guid requestId, long cityKey, DateOnly dateFrom, TaxiOfferFacts offer, string chip, CancellationToken ct)
    {
        var booking = await db.Set<TaxiBooking>().AsNoTracking()
            .Where(b => b.OfferId == offer.Id && BookingStates.Active.Contains(b.Status))
            .Select(b => new { b.Id, b.Status, b.TravellerUserId, b.TripId })
            .FirstOrDefaultAsync(ct);
        if (booking is null) return null;
        if (await store.TryMoveOfferAsync(db, offer.Id, [OfferStates.Confirmed], OfferStates.Withdrawn, null, chip, ct) is null) return null;

        if (booking.Status == BookingStates.InProgress)
        {
            var end = await store.EndRequestAsync(db, requestId, RequestStates.Cancelled, RequestEndReasons.DriverLeftTrip, chip, BookingCancelledBy.Driver, ct);
            if (end is null) return null;
            return end.Signals with { FreedDriverListingIds = [.. end.Signals.FreedDriverListingIds, offer.DriverListingId] };
        }

        if (await store.TryMoveBookingAsync(db, booking.Id, [BookingStates.Booked], BookingStates.Cancelled, BookingCancelledBy.Driver, chip, ct: ct) is null)
            return null;
        await scheduler.CancelAsync(db, DueActionKinds.BookingStart, booking.Id, ct);
        await scheduler.CancelAsync(db, DueActionKinds.BookingAutoComplete, booking.Id, ct);
        var reopened = await store.ReopenRequestAsync(db, requestId, TaxiDispatchRules.AcceptBatch, ct);
        if (reopened is not null)
            await scheduler.AddAtAsync(db, DueActionKinds.RequestStaleExpire, requestId, requestId, (int)reopened.Version,
                TaxiDispatchRules.StaleDueAt(dateFrom), ct);
        await pushes.NotifyAsync(db, new TaxiNotice(TaxiNoticeKind.DriverWithdrew, booking.TravellerUserId, booking.TripId, requestId, offer.Id), ct);
        return await TaxiOfferRows.SignalsAsync(db, requestId, cityKey, [offer.DriverListingId], ct);
    }

    // A selection that ends without a booking (declined by the driver, confirm window over, driver gone): the offer moves,
    // its confirm timer stops, the request goes back to Collecting or Open (2.3) and the traveller is told. Caller holds the
    // request lock. null = the offer was not Selected any more.
    public async Task<TaxiClosureResult?> LapseSelectionInTxAsync(ApplicationDbContext context, Guid requestId, TaxiOfferFacts offer,
        string to, string endReason, string? chip, CancellationToken ct)
    {
        var moved = await store.TryMoveOfferAsync(context, offer.Id, SelectedOnly, to, endReason, chip, ct);
        if (moved is null) return null;
        await scheduler.CancelAsync(context, DueActionKinds.ConfirmExpire, offer.Id, ct);
        var reopened = await store.ReopenRequestAsync(context, requestId, 0, ct);
        var change = await TaxiOfferRows.ChangeOfAsync(context, requestId, ct);
        if (reopened is not null && change is not null)
            await pushes.NotifyAsync(context, new TaxiNotice(TaxiNoticeKind.SelectionLapsed, change.TravellerUserId, change.TripId, requestId, offer.Id), ct);
        var city = reopened?.DispatchCityKey ?? await context.Set<TaxiRequest>().AsNoTracking()
            .Where(r => r.Id == requestId).Select(r => r.DispatchCityKey).FirstAsync(ct);
        return new TaxiClosureResult([city], [offer.DriverListingId], change is null ? [] : [change]);
    }

    private async Task<TaxiActionResult?> OwnedAsync(Guid offerId, Guid driverUserId, CancellationToken ct)
    {
        var offer = await TaxiOfferRows.FindAsync(db, offerId, ct);
        if (offer is null) return TaxiActionResult.Fail(TaxiRequestErrorTypes.OfferNotFound);
        return offer.DriverUserId == driverUserId ? null : TaxiActionResult.Fail(TaxiRequestErrorTypes.NotOfferDriver);
    }

    // Request row FOR UPDATE first, then a fresh read of the offer (2.4).
    private async Task<(TaxiRequest? Request, TaxiOfferFacts? Offer)> LockAsync(Guid offerId, CancellationToken ct)
    {
        var first = await TaxiOfferRows.FindAsync(db, offerId, ct);
        if (first is null) return (null, null);
        var request = await store.LockRequestAsync(db, first.RequestId, ct);
        var offer = await TaxiOfferRows.FindAsync(db, offerId, ct);
        return (request, offer);
    }

    private static TaxiActionResult Done(TaxiOfferFacts offer) => new(null, OfferId: offer.Id, RequestId: offer.RequestId);

    private sealed class CountRow
    {
        public int AcceptedCount { get; set; }
        public int AcceptTarget { get; set; }
    }
}
