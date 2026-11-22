using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using RentNearBy.Core.Entities.TaxiRequests;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services.TaxiRequests;

// MASTER_PLAN B2-6, booking side: the confirm handshake (P1 lock and check), end trip, auto complete and rating.
public sealed class TaxiBookingService(
    ApplicationDbContext db,
    TaxiRequestStore store,
    TaxiOfferService offers,
    ITaxiPushWriter pushes,
    IDueActionScheduler scheduler,
    ILogger<TaxiBookingService> logger,
    TaxiRequestMetrics? metrics = null,
    RentNearBy.Infrastructure.Services.Owner.IOwnerCalendarSync? calendar = null)
{
    private const int TripNameMax = 80, NameMax = 120;

    public const string InsertRatingSql = """
        INSERT INTO "TaxiRatings" ("BookingId", "DriverListingId", "Stars", "CreatedAt") VALUES (@booking, @driver, @stars, now())
        ON CONFLICT ("BookingId") DO NOTHING
        """;

    public const string BumpVersionSql = """
        UPDATE "TaxiRequests" SET "Version" = "Version" + 1, "UpdatedAt" = now() WHERE "Id" = @id
        """;

    public static double? RatingAverage(int sum, int count)
        => count <= 0 ? null : Math.Round((double)sum / count, 1, MidpointRounding.AwayFromZero);

    public static bool IsValidStars(int stars) => stars is >= 1 and <= 5;

    public static object DatesData(TaxiDateRange clash)
        => new { from = clash.From.ToString("yyyy-MM-dd"), to = clash.To.ToString("yyyy-MM-dd") };

    public Task<TaxiActionResult> ConfirmAsync(Guid offerId, Guid driverUserId, CancellationToken ct = default)
        => ConfirmAsync(offerId, driverUserId, new EfDriverCalendarSql(db), ct);

    // Test seam: the calendar must run on this service's context (same connection and transaction).
    internal async Task<TaxiActionResult> ConfirmAsync(Guid offerId, Guid driverUserId, IDriverCalendarSql calendar, CancellationToken ct)
    {
        var result = await PgRetry.OnceAsync(db, c => ConfirmUnitAsync(offerId, driverUserId, calendar, c), ct, logger);
        if (result is { Ok: true, BookingId: { } bookingId }) await CloseOverlappingOffersAsync(bookingId, ct);
        return result;
    }

    // Live offers of the booked driver on other requests whose dates overlap the booking. Ordered by request id so the
    // follow-up units below take request locks in one order (2.4).
    public static readonly string OverlappingLiveOffersSql = $"""
        SELECT o."Id" AS "OfferId", o."RequestId"
        FROM "TaxiRequestOffers" o JOIN "TaxiRequests" r ON r."Id" = o."RequestId"
        WHERE o."DriverListingId" = @driver AND o."RequestId" <> @request
          AND o."Status" IN ('{OfferStates.Accepted}', '{OfferStates.Selected}')
          AND r."DateFrom" <= @to AND r."DateTo" >= @from
        ORDER BY o."RequestId", o."Id"
        """;

    // After the booking commit: other travellers stop seeing a driver who can no longer serve their dates. Each request is
    // its own unit (request row lock only, never held together with the booking's locks). A failure leaves the offer to
    // the checks at select and confirm, so it is logged, not raised.
    private async Task CloseOverlappingOffersAsync(Guid bookingId, CancellationToken ct)
    {
        var booking = await db.Set<TaxiBooking>().AsNoTracking().Where(b => b.Id == bookingId)
            .Select(b => new { b.DriverListingId, b.RequestId, b.DateFrom, b.DateTo, b.Status }).FirstOrDefaultAsync(ct);
        if (booking is null || !BookingStates.Active.Contains(booking.Status)) return;
        var rows = await db.Database.SqlQueryRaw<OverlapOfferRow>(OverlappingLiveOffersSql,
            new NpgsqlParameter("driver", NpgsqlDbType.Uuid) { Value = booking.DriverListingId },
            new NpgsqlParameter("request", NpgsqlDbType.Uuid) { Value = booking.RequestId },
            new NpgsqlParameter("from", NpgsqlDbType.Date) { Value = booking.DateFrom },
            new NpgsqlParameter("to", NpgsqlDbType.Date) { Value = booking.DateTo }).ToListAsync(ct);
        foreach (var row in rows)
        {
            try
            {
                await PgRetry.OnceAsync(db, c => CloseOverlapUnitAsync(row.RequestId, row.OfferId, c), ct, logger);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "taxi.booking.close_overlap failed: {RequestId} {OfferId}", row.RequestId, row.OfferId);
            }
        }
    }

    private async Task CloseOverlapUnitAsync(Guid requestId, Guid offerId, CancellationToken ct)
    {
        TaxiClosureResult? signals = null;
        await using (var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct))
        {
            var request = await store.LockRequestAsync(db, requestId, ct);
            var offer = await TaxiOfferRows.FindAsync(db, offerId, ct);
            if (request is null || offer is null) return;
            if (offer.Status == OfferStates.Selected)
            {
                signals = await offers.LapseSelectionInTxAsync(db, requestId, offer, OfferStates.Closed, OfferEndReasons.Booked, null, ct);
            }
            else if (offer.Status == OfferStates.Accepted
                     && await store.TryMoveOfferAsync(db, offerId, [OfferStates.Accepted], OfferStates.Closed, OfferEndReasons.Booked, null, ct) is { } closed)
            {
                var change = new TaxiRequestChange(request.Id, request.TripId, request.TravellerUserId, closed.Version, request.Status);
                if (await store.ReopenRequestAsync(db, requestId, 0, ct) is { } reopened) change = reopened.Change;
                signals = new TaxiClosureResult([request.DispatchCityKey], [], [change]);
            }
            if (signals is null) return;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        metrics?.OfferEnded(OfferStates.Closed);
        await store.AfterCommitAsync(signals);
    }

    private sealed class OverlapOfferRow
    {
        public Guid OfferId { get; set; }
        public Guid RequestId { get; set; }
    }

    public Task<TaxiActionResult> EndByTravellerAsync(Guid requestId, Guid travellerUserId, CancellationToken ct = default)
        => PgRetry.OnceAsync(db, c => EndByTravellerUnitAsync(requestId, travellerUserId, c), ct, logger);

    public Task<TaxiActionResult> EndByDriverAsync(Guid bookingId, Guid driverUserId, CancellationToken ct = default)
        => PgRetry.OnceAsync(db, c => EndByDriverUnitAsync(bookingId, driverUserId, c), ct, logger);

    public Task<TaxiActionResult> RateAsync(Guid requestId, Guid travellerUserId, int stars, CancellationToken ct = default)
    {
        if (!IsValidStars(stars)) throw new ArgumentOutOfRangeException(nameof(stars), stars, "Stars must be 1 to 5");
        return PgRetry.OnceAsync(db, c => RateUnitAsync(requestId, travellerUserId, (short)stars, c), ct, logger);
    }

    // Lock order 2.4 in a READ COMMITTED transaction: request row, then the driver's TaxiListings row, then (only then) the
    // overlap query, offers, the booking, due actions, outbox rows.
    private async Task<TaxiActionResult> ConfirmUnitAsync(Guid offerId, Guid driverUserId, IDriverCalendarSql calendar, CancellationToken ct)
    {
        var first = await TaxiOfferRows.FindAsync(db, offerId, ct);
        if (first is null) return TaxiActionResult.Fail(TaxiRequestErrorTypes.OfferNotFound);
        if (first.DriverUserId != driverUserId) return TaxiActionResult.Fail(TaxiRequestErrorTypes.NotOfferDriver);

        TaxiClosureResult signals;
        TaxiActionResult result;
        await using (var tx = await DriverCalendar.BeginConfirmTransactionAsync(db, ct))
        {
            var request = await store.LockRequestAsync(db, first.RequestId, ct);
            var offer = await TaxiOfferRows.FindAsync(db, offerId, ct);
            if (request is null || offer is null) return TaxiActionResult.Fail(TaxiRequestErrorTypes.OfferNotFound);
            if (offer.Status == OfferStates.Confirmed) return await ConfirmedAsync(offer, ct);
            if (offer.Status != OfferStates.Selected)
                return offer is { Status: OfferStates.Missed, EndReason: OfferEndReasons.ConfirmTimeout }
                    ? TaxiActionResult.Fail(TaxiRequestErrorTypes.ConfirmWindowPassed)
                    : TaxiActionResult.StateOf(TaxiRequestErrorTypes.OfferStateInvalid, offer.Status);
            if (request.Status != RequestStates.Selected || request.SelectedOfferId != offer.Id)
                return TaxiActionResult.StateOf(TaxiRequestErrorTypes.OfferStateInvalid, offer.Status);
            if (!offer.ConfirmOpen) return TaxiActionResult.Fail(TaxiRequestErrorTypes.ConfirmWindowPassed);

            var range = new TaxiDateRange(request.DateFrom, request.DateTo);
            var check = await DriverCalendar.LockAndCheckAsync(calendar, offer.DriverListingId, range, null, ct);
            if (check.Outcome == DriverCheckOutcome.DriverMissing || !check.Driver!.IsBookable)
            {
                var lapsed = await offers.LapseSelectionInTxAsync(db, request.Id, offer, OfferStates.Closed, OfferEndReasons.ListingRemoved, null, ct);
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                signals = lapsed ?? TaxiClosureResult.Empty;
                result = TaxiActionResult.StateOf(TaxiRequestErrorTypes.OfferStateInvalid, OfferStates.Closed);
            }
            else if (check.Outcome == DriverCheckOutcome.Overlap)
            {
                // O8: the driver is booked on these dates elsewhere, so the selection lapses at once (no wait for the
                // confirm timer): the request goes back to Collecting with its other accepts and the traveller is told.
                var lapsed = await offers.LapseSelectionInTxAsync(db, request.Id, offer, OfferStates.Closed, OfferEndReasons.Booked, null, ct);
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                metrics?.OverlapRejected();
                signals = lapsed ?? TaxiClosureResult.Empty;
                result = TaxiActionResult.Fail(TaxiRequestErrorTypes.DatesAlreadyBooked, DatesData(check.Clash!.Value));
            }
            else
            {
                (signals, result) = await BookAsync(request, offer, range, ct);
                await tx.CommitAsync(ct);
            }
        }
        await store.AfterCommitAsync(signals);
        return result;
    }

    private async Task<(TaxiClosureResult, TaxiActionResult)> BookAsync(TaxiRequest request, TaxiOfferFacts offer, TaxiDateRange range, CancellationToken ct)
    {
        await store.TryMoveRequestAsync(db, request.Id, [RequestStates.Selected], RequestStates.Booked, ct: ct);
        await store.TryMoveOfferAsync(db, offer.Id, [OfferStates.Selected], OfferStates.Confirmed, ct: ct);
        var notSelected = await store.CloseOffersAsync(db, request.Id, [OfferStates.Accepted, OfferStates.Selected], OfferStates.NotSelected, OfferEndReasons.Booked, offer.Id, ct);
        var rang = await store.CloseOffersAsync(db, request.Id, [OfferStates.Ringing], OfferStates.Closed, OfferEndReasons.Booked, offer.Id, ct);

        var booking = await BuildBookingAsync(request, offer, range, ct);
        db.Set<TaxiBooking>().Add(booking);
        await db.SaveChangesAsync(ct);
        if (calendar is not null) await calendar.SyncTaxiBookingAsync(db, booking.Id, ct);

        await scheduler.CancelForRequestAsync(db, request.Id, DueActionKinds.All, ct);
        await scheduler.CancelAsync(db, DueActionKinds.ConfirmExpire, offer.Id, ct);
        foreach (var o in notSelected.Concat(rang))
        {
            await scheduler.CancelAsync(db, DueActionKinds.OfferExpire, o.OfferId, ct);
            await scheduler.CancelAsync(db, DueActionKinds.ConfirmExpire, o.OfferId, ct);
        }
        await scheduler.AddAtAsync(db, DueActionKinds.BookingStart, booking.Id, request.Id, 0, TaxiDispatchRules.BookingStartAt(range.From), ct);
        await scheduler.AddAtAsync(db, DueActionKinds.BookingAutoComplete, booking.Id, request.Id, 0, TaxiDispatchRules.AutoCompleteAt(range.To), ct);

        foreach (var o in notSelected)
            await pushes.NotifyAsync(db, new TaxiNotice(TaxiNoticeKind.NotSelected, o.DriverUserId, request.TripId, request.Id, o.OfferId), ct);
        foreach (var o in rang)
            await pushes.RingEndAsync(db, new RingEndPush(o.OfferId, request.Id, o.DriverUserId, OfferEndReasons.Booked), ct);
        await pushes.NotifyAsync(db, new TaxiNotice(TaxiNoticeKind.Booked, request.TravellerUserId, request.TripId, request.Id, offer.Id,
            NullIfEmpty(booking.DriverName)), ct);

        var signals = await TaxiOfferRows.SignalsAsync(db, request.Id, request.DispatchCityKey,
            notSelected.Concat(rang).Select(o => o.DriverListingId), ct);
        await db.SaveChangesAsync(ct);
        metrics?.OfferEnded(OfferStates.Confirmed);
        return (signals, new TaxiActionResult(null, OfferId: offer.Id, BookingId: booking.Id, RequestId: request.Id));
    }

    private async Task<TaxiBooking> BuildBookingAsync(TaxiRequest request, TaxiOfferFacts offer, TaxiDateRange range, CancellationToken ct)
    {
        var tripName = await db.Trips.AsNoTracking().Where(t => t.Id == request.TripId).Select(t => t.Title).FirstOrDefaultAsync(ct);
        var vehicle = await db.TaxiListings.AsNoTracking().Where(t => t.Id == offer.DriverListingId)
            .Select(t => db.TaxiVehicleTypes.Where(v => v.Id == t.VehicleTypeId).Select(v => v.Name).FirstOrDefault())
            .FirstOrDefaultAsync(ct);
        var names = await db.Users.AsNoTracking().Where(u => u.Id == offer.DriverUserId || u.Id == request.TravellerUserId)
            .Select(u => new { u.Id, u.Name }).ToListAsync(ct);
        string NameOf(Guid id) => Clip(names.FirstOrDefault(n => n.Id == id)?.Name, NameMax);

        return new TaxiBooking
        {
            Id = Guid.NewGuid(),
            RequestId = request.Id,
            OfferId = offer.Id,
            TripId = request.TripId,
            DriverListingId = offer.DriverListingId,
            DriverUserId = offer.DriverUserId,
            TravellerUserId = request.TravellerUserId,
            DateFrom = range.From,
            DateTo = range.To,
            Status = BookingStates.Booked,
            TripName = Clip(tripName, TripNameMax),
            PickupCityName = Clip(request.PickupCityName ?? request.DropCityName, NameMax),
            DriverName = NameOf(offer.DriverUserId),
            VehicleName = Clip(vehicle, NameMax),
            Seats = request.Seats,
            TravellerName = NameOf(request.TravellerUserId),
            BookedAt = DateTime.UtcNow,
        };
    }

    private async Task<TaxiActionResult> ConfirmedAsync(TaxiOfferFacts offer, CancellationToken ct)
    {
        var bookingId = await db.Set<TaxiBooking>().AsNoTracking().Where(b => b.OfferId == offer.Id)
            .OrderByDescending(b => b.BookedAt).Select(b => (Guid?)b.Id).FirstOrDefaultAsync(ct);
        return new TaxiActionResult(null, OfferId: offer.Id, BookingId: bookingId, RequestId: offer.RequestId);
    }

    private async Task<TaxiActionResult> EndByTravellerUnitAsync(Guid requestId, Guid travellerUserId, CancellationToken ct)
    {
        TaxiClosureResult? signals;
        await using (var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct))
        {
            var request = await store.LockRequestAsync(db, requestId, ct);
            if (request is null) return TaxiActionResult.Fail(TaxiRequestErrorTypes.TaxiRequestNotFound);
            if (request.TravellerUserId != travellerUserId) return TaxiActionResult.Fail(TaxiRequestErrorTypes.NotTripOwner);
            if (request.Status == RequestStates.Completed) return new TaxiActionResult(null, RequestId: requestId);
            var bookingId = request.Status == RequestStates.InProgress ? await ActiveBookingIdAsync(db, requestId, ct) : null;
            if (bookingId is null)
                return TaxiActionResult.Fail(TaxiRequestErrorTypes.TaxiRequestStateInvalid, new { state = request.Status, version = request.Version });
            signals = await CompleteInTxAsync(db, requestId, bookingId.Value, BookingCompletedBy.Traveller, ct);
            if (signals is null)
                return TaxiActionResult.Fail(TaxiRequestErrorTypes.TaxiRequestStateInvalid, new { state = request.Status, version = request.Version });
            await tx.CommitAsync(ct);
        }
        await store.AfterCommitAsync(signals);
        return new TaxiActionResult(null, RequestId: requestId);
    }

    private async Task<TaxiActionResult> EndByDriverUnitAsync(Guid bookingId, Guid driverUserId, CancellationToken ct)
    {
        var first = await db.Set<TaxiBooking>().AsNoTracking().Where(b => b.Id == bookingId)
            .Select(b => new { b.RequestId, b.DriverUserId }).FirstOrDefaultAsync(ct);
        if (first is null) return TaxiActionResult.Fail(TaxiRequestErrorTypes.OfferNotFound);
        if (first.DriverUserId != driverUserId) return TaxiActionResult.Fail(TaxiRequestErrorTypes.NotOfferDriver);

        TaxiClosureResult? signals;
        await using (var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct))
        {
            await store.LockRequestAsync(db, first.RequestId, ct);
            var status = await db.Set<TaxiBooking>().AsNoTracking().Where(b => b.Id == bookingId).Select(b => b.Status).FirstAsync(ct);
            if (status == BookingStates.Completed) return new TaxiActionResult(null, BookingId: bookingId, RequestId: first.RequestId);
            if (status != BookingStates.InProgress) return TaxiActionResult.StateOf(TaxiRequestErrorTypes.OfferStateInvalid, status);
            signals = await CompleteInTxAsync(db, first.RequestId, bookingId, BookingCompletedBy.Driver, ct);
            if (signals is null) return TaxiActionResult.StateOf(TaxiRequestErrorTypes.OfferStateInvalid, status);
            await tx.CommitAsync(ct);
        }
        await store.AfterCommitAsync(signals);
        return new TaxiActionResult(null, BookingId: bookingId, RequestId: first.RequestId);
    }

    // End trip (InProgress only) and auto complete (Booked or InProgress, decision 15). The caller holds the request lock
    // and commits. The ride counter is the last statement. null = the booking was not in a state to complete.
    public async Task<TaxiClosureResult?> CompleteInTxAsync(ApplicationDbContext context, Guid requestId, Guid bookingId, string completedBy, CancellationToken ct)
    {
        string[] from = completedBy == BookingCompletedBy.Auto ? [BookingStates.Booked, BookingStates.InProgress] : [BookingStates.InProgress];
        var moved = await store.TryMoveBookingAsync(context, bookingId, from, BookingStates.Completed, completedBy: completedBy, ct: ct);
        if (moved is null) return null;
        string[] requestFrom = completedBy == BookingCompletedBy.Auto ? [RequestStates.Booked, RequestStates.InProgress] : [RequestStates.InProgress];
        var request = await store.TryMoveRequestAsync(context, requestId, requestFrom, RequestStates.Completed, ct: ct);
        await scheduler.CancelAsync(context, DueActionKinds.BookingStart, bookingId, ct);
        await scheduler.CancelAsync(context, DueActionKinds.BookingAutoComplete, bookingId, ct);

        var b = await context.Set<TaxiBooking>().AsNoTracking().Where(x => x.Id == bookingId)
            .Select(x => new { x.TripId, x.TravellerUserId, x.DriverUserId, x.DriverName, x.TravellerName }).FirstAsync(ct);
        if (completedBy == BookingCompletedBy.Traveller)
            await pushes.NotifyAsync(context, new TaxiNotice(TaxiNoticeKind.TripEnded, b.DriverUserId, b.TripId, requestId, null, NullIfEmpty(b.TravellerName)), ct);
        if (completedBy == BookingCompletedBy.Driver)
            await pushes.NotifyAsync(context, new TaxiNotice(TaxiNoticeKind.TripEnded, b.TravellerUserId, b.TripId, requestId, null, NullIfEmpty(b.DriverName)), ct);
        if (completedBy != BookingCompletedBy.Traveller)
            await pushes.NotifyAsync(context, new TaxiNotice(TaxiNoticeKind.RateDriver, b.TravellerUserId, b.TripId, requestId, null, NullIfEmpty(b.DriverName)), ct);

        var change = await TaxiOfferRows.ChangeOfAsync(context, requestId, ct);
        await context.SaveChangesAsync(ct);
        await context.TaxiListings.Where(t => t.Id == moved.DriverListingId)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RidesCompleted, t => t.RidesCompleted + 1), ct);
        // Driver's lifetime counter (D-06): only a ride started in the app; Users is the last lock.
        await TripCompletionCounter.CountTaxiBookingInTxAsync(context, bookingId, ct);
        return new TaxiClosureResult(request is null ? [] : [request.DispatchCityKey], [moved.DriverListingId], change is null ? [] : [change]);
    }

    // Booked -> InProgress at 00:00 IST of the first needed day. The caller holds the request lock.
    public async Task<TaxiClosureResult?> StartInTxAsync(ApplicationDbContext context, Guid requestId, Guid bookingId, CancellationToken ct)
    {
        var moved = await store.TryMoveBookingAsync(context, bookingId, [BookingStates.Booked], BookingStates.InProgress, ct: ct);
        if (moved is null) return null;
        await store.TryMoveRequestAsync(context, requestId, [RequestStates.Booked], RequestStates.InProgress, ct: ct);
        var change = await TaxiOfferRows.ChangeOfAsync(context, requestId, ct);
        return new TaxiClosureResult([], [], change is null ? [] : [change]);
    }

    // T10: once per booking, stars only. Same value again = 200, another value = AlreadyRated. A trip the driver left (or an
    // admin cancelled) while running is rated too (O13); RidesCompleted moves only in CompleteInTxAsync.
    private async Task<TaxiActionResult> RateUnitAsync(Guid requestId, Guid travellerUserId, short stars, CancellationToken ct)
    {
        TaxiClosureResult signals;
        await using (var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct))
        {
            var request = await store.LockRequestAsync(db, requestId, ct);
            if (request is null) return TaxiActionResult.Fail(TaxiRequestErrorTypes.TaxiRequestNotFound);
            if (request.TravellerUserId != travellerUserId) return TaxiActionResult.Fail(TaxiRequestErrorTypes.NotTripOwner);
            if (request.Status is not (RequestStates.Completed or RequestStates.Cancelled)) return TaxiActionResult.Fail(TaxiRequestErrorTypes.RatingNotAllowed);
            var booking = await db.Set<TaxiBooking>().AsNoTracking()
                .Where(b => b.RequestId == requestId)
                .OrderByDescending(b => b.BookedAt).Select(b => new { b.Id, b.DriverListingId, b.Status, b.StartedAt }).FirstOrDefaultAsync(ct);
            if (booking is null || !TaxiRatingRules.CanRate(request.Status, request.EndReason, booking.Status, booking.StartedAt))
                return TaxiActionResult.Fail(TaxiRequestErrorTypes.RatingNotAllowed);

            var inserted = await db.Database.ExecuteSqlRawAsync(InsertRatingSql,
            [
                new NpgsqlParameter("booking", NpgsqlDbType.Uuid) { Value = booking.Id },
                new NpgsqlParameter("driver", NpgsqlDbType.Uuid) { Value = booking.DriverListingId },
                new NpgsqlParameter("stars", NpgsqlDbType.Smallint) { Value = stars },
            ], ct);
            if (inserted == 0)
            {
                var existing = await db.Set<TaxiRating>().AsNoTracking().Where(r => r.BookingId == booking.Id).Select(r => r.Stars).FirstAsync(ct);
                return existing == stars
                    ? new TaxiActionResult(null, BookingId: booking.Id, RequestId: requestId)
                    : TaxiActionResult.Fail(TaxiRequestErrorTypes.AlreadyRated);
            }
            await db.Database.ExecuteSqlRawAsync(BumpVersionSql, [TaxiOfferRows.Uuid("id", requestId)], ct);
            var change = await TaxiOfferRows.ChangeOfAsync(db, requestId, ct);
            signals = new TaxiClosureResult([], [], change is null ? [] : [change]);
            await db.TaxiListings.Where(t => t.Id == booking.DriverListingId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.RatingSum, t => t.RatingSum + stars)
                    .SetProperty(t => t.RatingCount, t => t.RatingCount + 1), ct);
            await tx.CommitAsync(ct);
            await store.AfterCommitAsync(signals);
            return new TaxiActionResult(null, BookingId: booking.Id, RequestId: requestId);
        }
    }

    internal static async Task<Guid?> ActiveBookingIdAsync(ApplicationDbContext context, Guid requestId, CancellationToken ct)
        => await context.Set<TaxiBooking>().AsNoTracking()
            .Where(b => b.RequestId == requestId && BookingStates.Active.Contains(b.Status))
            .Select(b => (Guid?)b.Id).FirstOrDefaultAsync(ct);

    private static string Clip(string? value, int max)
    {
        var v = value?.Trim() ?? string.Empty;
        return v.Length <= max ? v : v[..max];
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
