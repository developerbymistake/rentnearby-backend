using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using RentNearBy.Core.Entities.TaxiRequests;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Interfaces.TaxiRequests;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services.TaxiRequests;

// Total = every offer the request ever had, in any state.
public sealed record TaxiLiveCounts(int Ringing, int Accepted, int Selected, int Total = 0);

public sealed record TaxiRequestMove(Guid RequestId, Guid TripId, Guid TravellerUserId, string FromState, string ToState, long Version, long DispatchCityKey)
{
    public TaxiRequestChange Change => new(RequestId, TripId, TravellerUserId, Version, ToState);
}

public sealed record TaxiOfferMove(Guid OfferId, Guid RequestId, Guid DriverListingId, Guid DriverUserId, string FromState, string ToState, long Version);

public sealed record TaxiBookingMove(Guid BookingId, Guid RequestId, Guid DriverListingId, Guid DriverUserId, string FromState, string ToState, long Version);

public sealed record TaxiClosedOffer(Guid OfferId, Guid DriverListingId, Guid DriverUserId, string FromState);

public sealed record TaxiCancelledBooking(Guid BookingId, Guid DriverListingId, Guid DriverUserId);

public sealed record TaxiEndResult(TaxiRequestMove Move, IReadOnlyList<TaxiClosedOffer> ClosedOffers, TaxiCancelledBooking? Booking, TaxiClosureResult Signals);

// MASTER_PLAN B2-10: the one place that moves requests, offers and bookings. Every method runs inside the caller's
// transaction, keeps the lock order of 2.4 (trip -> request -> offers -> bookings -> due actions -> outbox) and changes a
// row only with "WHERE Status IN (from)". Nothing here commits or calls SaveChanges.
public sealed class TaxiRequestStore(
    ITaxiPushWriter pushes,
    IDueActionScheduler scheduler,
    IOutboxSignal outboxSignal,
    IDueActionsSignal dueSignal,
    IMatcherSignal matcherSignal,
    ITaxiRequestEvents events,
    ILogger<TaxiRequestStore> logger)
{
    public Task LockTripAsync(ApplicationDbContext db, Guid tripId, CancellationToken ct = default)
        => db.Database.ExecuteSqlRawAsync(TaxiRequestStoreSql.LockTrip, [Uuid("id", tripId)], ct);

    public async Task<TaxiRequest?> LockRequestAsync(ApplicationDbContext db, Guid requestId, CancellationToken ct = default)
        => await db.Set<TaxiRequest>().FromSqlRaw(TaxiRequestStoreSql.LockRequest, Uuid("id", requestId))
            .AsNoTracking().SingleOrDefaultAsync(ct);

    public async Task<TaxiLiveCounts> GetLiveCountsAsync(ApplicationDbContext db, Guid requestId, CancellationToken ct = default)
    {
        var row = await db.Database.SqlQueryRaw<LiveCountsRow>(TaxiRequestStoreSql.LiveCounts, Uuid("id", requestId)).SingleAsync(ct);
        return new TaxiLiveCounts(row.Ringing, row.Accepted, row.Selected, row.Total);
    }

    public async Task<TaxiRequestMove?> TryMoveRequestAsync(ApplicationDbContext db, Guid requestId, IReadOnlyCollection<string> from, string to,
        string? endReason = null, string? chip = null, CancellationToken ct = default)
    {
        var sql = TaxiRequestStoreSql.MoveRequest(from, to);
        var rows = await db.Database.SqlQueryRaw<RequestMoveRow>(sql,
            Uuid("id", requestId), Text("endReason", endReason), Text("chip", chip)).ToListAsync(ct);
        return rows.Count == 0 ? null : rows[0].ToMove();
    }

    public Task<TaxiOfferMove?> TryMoveOfferAsync(ApplicationDbContext db, Guid offerId, IReadOnlyCollection<string> from, string to,
        string? endReason = null, string? chip = null, CancellationToken ct = default)
        => MoveOfferCoreAsync(db, offerId, from, to, endReason, chip, bumpVersion: true, ct);

    public Task<TaxiBookingMove?> TryMoveBookingAsync(ApplicationDbContext db, Guid bookingId, IReadOnlyCollection<string> from, string to,
        string? cancelledBy = null, string? chip = null, string? completedBy = null, CancellationToken ct = default)
        => MoveBookingCoreAsync(db, bookingId, from, to, cancelledBy, chip, completedBy, bumpVersion: true, ct);

    public async Task<IReadOnlyList<TaxiClosedOffer>> CloseOffersAsync(ApplicationDbContext db, Guid requestId, IReadOnlyCollection<string> from, string to,
        string? endReason, Guid? exceptOfferId = null, CancellationToken ct = default)
    {
        await LockRequestRowAsync(db, requestId, ct);
        var closed = await CloseOffersCoreAsync(db, requestId, from, to, endReason, exceptOfferId, ct);
        if (closed.Count > 0) await BumpVersionAsync(db, requestId, ct);
        return closed;
    }

    // Moves the request to Cancelled or Expired, closes its live offers, cancels an active booking and every pending
    // timer of the request, and writes the pushes as rows. null = the request was not in a state that can end so.
    public async Task<TaxiEndResult?> EndRequestAsync(ApplicationDbContext db, Guid requestId, string to, string endReason,
        string? chip = null, string? cancelledBy = null, CancellationToken ct = default)
    {
        TaxiRequestStoreSql.EnsureEndReason(to, endReason);
        var from = TaxiRequestStoreSql.EndableFrom(to, endReason);
        var move = await TryMoveRequestAsync(db, requestId, from, to, endReason, chip, ct);
        if (move is null) return null;

        var offerReason = TaxiRequestStoreSql.OfferReasonFor(to);
        var closed = await CloseOffersCoreAsync(db, requestId, OfferStates.Live, OfferStates.Closed, offerReason, null, ct);

        var bookingRows = await db.Database.SqlQueryRaw<CancelledBookingRow>(TaxiRequestStoreSql.CancelActiveBookings,
            Uuid("id", requestId), Text("cancelledBy", cancelledBy ?? BookingCancelledBy.System), Text("chip", chip)).ToListAsync(ct);
        var booking = bookingRows.Count == 0 ? null : new TaxiCancelledBooking(bookingRows[0].Id, bookingRows[0].DriverListingId, bookingRows[0].DriverUserId);

        return await FinishEndAsync(pushes, scheduler, db, move, closed, booking, cancelledBy, endReason, ct);
    }

    // The part of EndRequestAsync after its row moves: timers, pushes and the closure result.
    internal static async Task<TaxiEndResult> FinishEndAsync(ITaxiPushWriter writer, IDueActionScheduler scheduler, ApplicationDbContext db,
        TaxiRequestMove move, IReadOnlyList<TaxiClosedOffer> closed, TaxiCancelledBooking? booking, string? cancelledBy, string endReason, CancellationToken ct)
    {
        await CancelTimersAsync(scheduler, db, move.RequestId, closed, booking, ct);
        await WriteEndPushesAsync(writer, db, move, TaxiRequestStoreSql.OfferReasonFor(move.ToState), closed, booking, cancelledBy, endReason, ct);

        var freed = closed.Select(o => o.DriverListingId).Concat(booking is null ? [] : [booking.DriverListingId]).Distinct().ToList();
        return new TaxiEndResult(move, closed, booking, new TaxiClosureResult([move.DispatchCityKey], freed, [move.Change]));
    }

    // Back to Collecting when a live Accepted offer exists (never from Booked, 2.3), else Open. Clears the selection and
    // raises AcceptTarget by extraAccepts within the cap. The exhausted mark is cleared only with extra accepts (a driver
    // withdraw, P10): a lapsed selection or a lost accept never rings new drivers on its own (O5); an exhausted request left
    // Open waits for ask more (TaxiDispatchRules.CanAskMore) or its stale expiry. Choose reminders (O7): armed when accepts
    // are left to choose from (Selected -> Collecting), cancelled when none is left. null = nothing to reopen.
    public async Task<TaxiRequestMove?> ReopenRequestAsync(ApplicationDbContext db, Guid requestId, int extraAccepts, CancellationToken ct = default)
    {
        if (extraAccepts < 0) throw new ArgumentOutOfRangeException(nameof(extraAccepts));
        var locked = await LockRequestAsync(db, requestId, ct);
        if (locked is null) return null;
        var hasAccepted = await db.Database.SqlQueryRaw<bool>(TaxiRequestStoreSql.HasAcceptedOffer, Uuid("id", requestId)).SingleAsync(ct);
        var target = TaxiRequestStoreSql.ReopenTarget(locked.Status, hasAccepted);
        if (target is null) return null;

        var rows = await db.Database.SqlQueryRaw<RequestMoveRow>(TaxiRequestStoreSql.Reopen(locked.Status, target),
            Uuid("id", requestId), new NpgsqlParameter("extra", NpgsqlDbType.Integer) { Value = extraAccepts }).ToListAsync(ct);
        if (rows.Count == 0) return null;
        var move = rows[0].ToMove();

        if (target == RequestStates.Collecting)
        {
            if (locked.Status == RequestStates.Selected) await ChooseReminders.ArmAsync(scheduler, db, requestId, locked.DateFrom, ct);
            return move;
        }
        await ChooseReminders.CancelAsync(scheduler, db, requestId, ct);
        return move;
    }

    // The only writers of TaxiListings.RingMissStreak (P26). Leaf statements: no FOR UPDATE, nothing read afterwards.
    public async Task<int?> AddRingMissAsync(ApplicationDbContext db, Guid driverListingId, CancellationToken ct = default)
    {
        var rows = await db.Database.SqlQueryRaw<int>(TaxiRequestStoreSql.AddRingMiss, Uuid("id", driverListingId)).ToListAsync(ct);
        return rows.Count == 0 ? null : rows[0];
    }

    public async Task<bool> ResetRingMissAsync(ApplicationDbContext db, Guid driverListingId, CancellationToken ct = default)
        => await db.Database.ExecuteSqlRawAsync(TaxiRequestStoreSql.ResetRingMiss, [Uuid("id", driverListingId)], ct) > 0;

    // After commit only. Every signal is best effort: the sweeper, the poll and the snapshot GET cover a lost one.
    public async Task AfterCommitAsync(TaxiClosureResult result)
    {
        Safe(() => outboxSignal.Wake(), "outbox wake");
        Safe(() => dueSignal.Wake(), "due actions wake");
        foreach (var city in result.CityKeys.Distinct()) Safe(() => matcherSignal.MarkDirty(city), "matcher mark dirty");
        foreach (var driver in result.FreedDriverListingIds.Distinct()) Safe(() => matcherSignal.MarkDriverFreed(driver), "matcher driver freed");
        foreach (var c in result.Changes)
        {
            try
            {
                await events.RequestChangedAsync(c.RequestId, c.TripId, c.TravellerUserId, c.Version, c.State);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "taxi.request.after_commit failed: {Step} {RequestId}", "request event", c.RequestId);
            }
        }
    }

    private void Safe(Action action, string step)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "taxi.request.after_commit failed: {Step}", step);
        }
    }

    internal static async Task WriteEndPushesAsync(ITaxiPushWriter writer, ApplicationDbContext db, TaxiRequestMove move, string offerReason,
        IReadOnlyList<TaxiClosedOffer> closed, TaxiCancelledBooking? booking, string? cancelledBy, string endReason, CancellationToken ct)
    {
        foreach (var o in closed)
        {
            if (o.FromState == OfferStates.Ringing)
                await writer.RingEndAsync(db, new RingEndPush(o.OfferId, move.RequestId, o.DriverUserId, offerReason), ct);
            else
                await writer.NotifyAsync(db, new TaxiNotice(TaxiNoticeKind.RequestClosed, o.DriverUserId, move.TripId, move.RequestId, o.OfferId), ct);
        }
        if (booking is not null && cancelledBy != BookingCancelledBy.Driver)
            await writer.NotifyAsync(db, new TaxiNotice(TaxiNoticeKind.BookingCancelled, booking.DriverUserId, move.TripId, move.RequestId), ct);
        if (TaxiRequestStoreSql.TravellerNoticeFor(endReason) is { } kind)
            await writer.NotifyAsync(db, new TaxiNotice(kind, move.TravellerUserId, move.TripId, move.RequestId), ct);
    }

    private static async Task CancelTimersAsync(IDueActionScheduler scheduler, ApplicationDbContext db, Guid requestId, IReadOnlyList<TaxiClosedOffer> closed, TaxiCancelledBooking? booking, CancellationToken ct)
    {
        await scheduler.CancelForRequestAsync(db, requestId, DueActionKinds.All, ct);
        foreach (var o in closed)
        {
            await scheduler.CancelAsync(db, DueActionKinds.OfferExpire, o.OfferId, ct);
            await scheduler.CancelAsync(db, DueActionKinds.ConfirmExpire, o.OfferId, ct);
        }
        if (booking is not null)
        {
            await scheduler.CancelAsync(db, DueActionKinds.BookingStart, booking.BookingId, ct);
            await scheduler.CancelAsync(db, DueActionKinds.BookingAutoComplete, booking.BookingId, ct);
        }
    }

    private static async Task<TaxiOfferMove?> MoveOfferCoreAsync(ApplicationDbContext db, Guid offerId, IReadOnlyCollection<string> from, string to,
        string? endReason, string? chip, bool bumpVersion, CancellationToken ct)
    {
        var sql = TaxiRequestStoreSql.MoveOffer(from, to);
        var requestIds = await db.Database.SqlQueryRaw<Guid>(TaxiRequestStoreSql.LockRequestOfOffer, Uuid("id", offerId)).ToListAsync(ct);
        if (requestIds.Count == 0) return null;
        var rows = await db.Database.SqlQueryRaw<OfferMoveRow>(sql, Uuid("id", offerId), Text("endReason", endReason), Text("chip", chip)).ToListAsync(ct);
        if (rows.Count == 0) return null;
        var r = rows[0];
        var version = bumpVersion ? await BumpVersionAsync(db, r.RequestId, ct) : 0;
        return new TaxiOfferMove(r.Id, r.RequestId, r.DriverListingId, r.DriverUserId, r.FromState, r.ToState, version);
    }

    private static async Task<TaxiBookingMove?> MoveBookingCoreAsync(ApplicationDbContext db, Guid bookingId, IReadOnlyCollection<string> from, string to,
        string? cancelledBy, string? chip, string? completedBy, bool bumpVersion, CancellationToken ct)
    {
        var sql = TaxiRequestStoreSql.MoveBooking(from, to);
        var requestIds = await db.Database.SqlQueryRaw<Guid>(TaxiRequestStoreSql.LockRequestOfBooking, Uuid("id", bookingId)).ToListAsync(ct);
        if (requestIds.Count == 0) return null;
        var rows = await db.Database.SqlQueryRaw<BookingMoveRow>(sql, Uuid("id", bookingId),
            Text("cancelledBy", cancelledBy), Text("chip", chip), Text("completedBy", completedBy)).ToListAsync(ct);
        if (rows.Count == 0) return null;
        var r = rows[0];
        var version = bumpVersion ? await BumpVersionAsync(db, r.RequestId, ct) : 0;
        return new TaxiBookingMove(r.Id, r.RequestId, r.DriverListingId, r.DriverUserId, r.FromState, r.ToState, version);
    }

    private static async Task<IReadOnlyList<TaxiClosedOffer>> CloseOffersCoreAsync(ApplicationDbContext db, Guid requestId, IReadOnlyCollection<string> from,
        string to, string? endReason, Guid? exceptOfferId, CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<OfferMoveRow>(TaxiRequestStoreSql.CloseOffers(from, to),
            Uuid("requestId", requestId), Text("endReason", endReason),
            new NpgsqlParameter("exceptId", NpgsqlDbType.Uuid) { Value = (object?)exceptOfferId ?? DBNull.Value }).ToListAsync(ct);
        return rows.Select(r => new TaxiClosedOffer(r.Id, r.DriverListingId, r.DriverUserId, r.FromState)).ToList();
    }

    private static Task LockRequestRowAsync(ApplicationDbContext db, Guid requestId, CancellationToken ct)
        => db.Database.ExecuteSqlRawAsync(TaxiRequestStoreSql.LockRequestRow, [Uuid("id", requestId)], ct);

    private static async Task<long> BumpVersionAsync(ApplicationDbContext db, Guid requestId, CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<long>(TaxiRequestStoreSql.BumpVersion, Uuid("id", requestId)).ToListAsync(ct);
        return rows.Count == 0 ? 0 : rows[0];
    }

    private static NpgsqlParameter Uuid(string name, Guid value) => new(name, NpgsqlDbType.Uuid) { Value = value };

    private static NpgsqlParameter Text(string name, string? value) => new(name, NpgsqlDbType.Varchar) { Value = (object?)value ?? DBNull.Value };

    private sealed class LiveCountsRow
    {
        public int Ringing { get; set; }
        public int Accepted { get; set; }
        public int Selected { get; set; }
        public int Total { get; set; }
    }

    private sealed class RequestMoveRow
    {
        public Guid Id { get; set; }
        public Guid TripId { get; set; }
        public Guid TravellerUserId { get; set; }
        public string FromState { get; set; } = "";
        public string ToState { get; set; } = "";
        public long Version { get; set; }
        public long DispatchCityKey { get; set; }

        public TaxiRequestMove ToMove() => new(Id, TripId, TravellerUserId, FromState, ToState, Version, DispatchCityKey);
    }

    private sealed class OfferMoveRow
    {
        public Guid Id { get; set; }
        public Guid RequestId { get; set; }
        public Guid DriverListingId { get; set; }
        public Guid DriverUserId { get; set; }
        public string FromState { get; set; } = "";
        public string ToState { get; set; } = "";
    }

    private sealed class BookingMoveRow
    {
        public Guid Id { get; set; }
        public Guid RequestId { get; set; }
        public Guid DriverListingId { get; set; }
        public Guid DriverUserId { get; set; }
        public string FromState { get; set; } = "";
        public string ToState { get; set; } = "";
    }

    private sealed class CancelledBookingRow
    {
        public Guid Id { get; set; }
        public Guid DriverListingId { get; set; }
        public Guid DriverUserId { get; set; }
    }
}

// SQL of the store. States are validated against TaxiRequestTransitions and written as literals, so every statement
// repeats its from states in the text (tested).
public static class TaxiRequestStoreSql
{
    public const string LockTrip = "SELECT 1 FROM \"Trips\" WHERE \"Id\" = @id FOR UPDATE";

    public const string LockRequest = "SELECT * FROM \"TaxiRequests\" WHERE \"Id\" = @id FOR UPDATE";

    public const string LockRequestRow = "SELECT 1 FROM \"TaxiRequests\" WHERE \"Id\" = @id FOR UPDATE";

    public const string LockRequestOfOffer = """
        SELECT r."Id" AS "Value" FROM "TaxiRequests" r
        JOIN "TaxiRequestOffers" o ON o."RequestId" = r."Id"
        WHERE o."Id" = @id
        FOR UPDATE OF r
        """;

    public const string LockRequestOfBooking = """
        SELECT r."Id" AS "Value" FROM "TaxiRequests" r
        JOIN "TaxiBookings" b ON b."RequestId" = r."Id"
        WHERE b."Id" = @id
        FOR UPDATE OF r
        """;

    public const string BumpVersion = """
        UPDATE "TaxiRequests" SET "Version" = "Version" + 1, "UpdatedAt" = now()
        WHERE "Id" = @id
        RETURNING "Version" AS "Value"
        """;

    public static readonly string LiveCounts = $"""
        SELECT count(*) FILTER (WHERE "Status" = '{OfferStates.Ringing}')::int AS "Ringing",
               count(*) FILTER (WHERE "Status" = '{OfferStates.Accepted}')::int AS "Accepted",
               count(*) FILTER (WHERE "Status" = '{OfferStates.Selected}')::int AS "Selected",
               count(*)::int AS "Total"
        FROM "TaxiRequestOffers" WHERE "RequestId" = @id
        """;

    public static readonly string HasAcceptedOffer = $"""
        SELECT EXISTS (SELECT 1 FROM "TaxiRequestOffers" WHERE "RequestId" = @id AND "Status" = '{OfferStates.Accepted}') AS "Value"
        """;

    public static readonly string CancelActiveBookings = $"""
        UPDATE "TaxiBookings" SET "Status" = '{BookingStates.Cancelled}', "CancelledBy" = @cancelledBy, "CancelChip" = @chip, "CancelledAt" = now()
        WHERE "RequestId" = @id AND "Status" IN ({List(BookingStates.Active)})
        RETURNING "Id", "DriverListingId", "DriverUserId"
        """;

    public static readonly string AddRingMiss = """
        UPDATE "TaxiListings" SET "RingMissStreak" = "RingMissStreak" + 1 WHERE "Id" = @id RETURNING "RingMissStreak"::int AS "Value"
        """;

    public static readonly string ResetRingMiss = """
        UPDATE "TaxiListings" SET "RingMissStreak" = 0 WHERE "Id" = @id AND "RingMissStreak" > 0
        """;

    public static string MoveRequest(IReadOnlyCollection<string> from, string to)
    {
        var fromList = Checked(TransitionKinds.Request, from, to);
        var end = RequestStates.IsFinal(to) ? ", \"EndReason\" = @endReason, \"CancelChip\" = @chip, \"ClosedAt\" = now()" : "";
        return $"""
            WITH old AS (SELECT "Id", "Status" FROM "TaxiRequests" WHERE "Id" = @id AND "Status" IN ({fromList}) FOR UPDATE)
            UPDATE "TaxiRequests" AS r SET "Status" = '{to}', "Version" = r."Version" + 1, "UpdatedAt" = now(){end}
            FROM old WHERE r."Id" = old."Id" AND r."Status" IN ({fromList})
            RETURNING r."Id", r."TripId", r."TravellerUserId", old."Status" AS "FromState", r."Status" AS "ToState", r."Version", r."DispatchCityKey"
            """;
    }

    public static string MoveOffer(IReadOnlyCollection<string> from, string to)
    {
        var fromList = Checked(TransitionKinds.Offer, from, to);
        return $"""
            WITH old AS (SELECT "Id", "Status" FROM "TaxiRequestOffers" WHERE "Id" = @id AND "Status" IN ({fromList}) FOR UPDATE)
            UPDATE "TaxiRequestOffers" AS o SET "Status" = '{to}', "EndReason" = COALESCE(@endReason, o."EndReason"), "Chip" = COALESCE(@chip, o."Chip"){OfferStamps(to)}
            FROM old WHERE o."Id" = old."Id" AND o."Status" IN ({fromList})
            RETURNING o."Id", o."RequestId", o."DriverListingId", o."DriverUserId", old."Status" AS "FromState", o."Status" AS "ToState"
            """;
    }

    public static string CloseOffers(IReadOnlyCollection<string> from, string to)
    {
        var fromList = Checked(TransitionKinds.Offer, from, to);
        return $"""
            WITH old AS (
                SELECT "Id", "Status" FROM "TaxiRequestOffers"
                WHERE "RequestId" = @requestId AND "Status" IN ({fromList}) AND (@exceptId IS NULL OR "Id" <> @exceptId)
                FOR UPDATE)
            UPDATE "TaxiRequestOffers" AS o SET "Status" = '{to}', "EndReason" = COALESCE(@endReason, o."EndReason"){OfferStamps(to)}
            FROM old WHERE o."Id" = old."Id" AND o."Status" IN ({fromList})
            RETURNING o."Id", o."RequestId", o."DriverListingId", o."DriverUserId", old."Status" AS "FromState", o."Status" AS "ToState"
            """;
    }

    public static string MoveBooking(IReadOnlyCollection<string> from, string to)
    {
        var fromList = Checked(TransitionKinds.Booking, from, to);
        var stamps = to switch
        {
            BookingStates.InProgress => ", \"StartedAt\" = now()",
            BookingStates.Completed => ", \"CompletedAt\" = now(), \"CompletedBy\" = @completedBy",
            BookingStates.Cancelled => ", \"CancelledAt\" = now(), \"CancelledBy\" = @cancelledBy, \"CancelChip\" = @chip",
            _ => "",
        };
        return $"""
            WITH old AS (SELECT "Id", "Status" FROM "TaxiBookings" WHERE "Id" = @id AND "Status" IN ({fromList}) FOR UPDATE)
            UPDATE "TaxiBookings" AS b SET "Status" = '{to}'{stamps}
            FROM old WHERE b."Id" = old."Id" AND b."Status" IN ({fromList})
            RETURNING b."Id", b."RequestId", b."DriverListingId", b."DriverUserId", old."Status" AS "FromState", b."Status" AS "ToState"
            """;
    }

    public static string Reopen(string from, string to)
    {
        var fromList = Checked(TransitionKinds.Request, [from], to);
        return $"""
            WITH old AS (SELECT "Id", "Status" FROM "TaxiRequests" WHERE "Id" = @id AND "Status" IN ({fromList}) FOR UPDATE)
            UPDATE "TaxiRequests" AS r SET "Status" = '{to}', "SelectedOfferId" = NULL, "QueueWaitStartedAt" = NULL,
                "QueueExhaustedAt" = CASE WHEN @extra > 0 THEN NULL ELSE r."QueueExhaustedAt" END,
                "AcceptTarget" = LEAST({TaxiDispatchRules.MaxAccepts}, r."AcceptTarget" + @extra)::smallint,
                "Version" = r."Version" + 1, "UpdatedAt" = now()
            FROM old WHERE r."Id" = old."Id" AND r."Status" IN ({fromList})
            RETURNING r."Id", r."TripId", r."TravellerUserId", old."Status" AS "FromState", r."Status" AS "ToState", r."Version", r."DispatchCityKey"
            """;
    }

    // null = nothing to reopen. Booked may only go back to Open (2.3); Collecting stays Collecting while an accept lives.
    public static string? ReopenTarget(string current, bool hasLiveAccepted)
    {
        if (current is not (RequestStates.Selected or RequestStates.Booked or RequestStates.Collecting)) return null;
        var target = current != RequestStates.Booked && hasLiveAccepted ? RequestStates.Collecting : RequestStates.Open;
        return TaxiRequestTransitions.CanMove(TransitionKinds.Request, current, target) ? target : null;
    }

    // 2.3: a running trip (InProgress) is cancelled only when the driver left it; admin cancel and account closure may
    // still end it so a request never stays stuck. Every other reason leaves InProgress alone.
    private static readonly string[] InProgressEndReasons =
        [RequestEndReasons.DriverLeftTrip, RequestEndReasons.AdminCancelled, RequestEndReasons.AccountClosed];

    public static IReadOnlyList<string> EndableFrom(string to, string endReason)
        => RequestStates.Active
            .Where(s => TaxiRequestTransitions.CanMove(TransitionKinds.Request, s, to))
            .Where(s => s != RequestStates.InProgress || InProgressEndReasons.Contains(endReason))
            .ToList();

    public static string OfferReasonFor(string requestTo)
        => requestTo == RequestStates.Cancelled ? OfferEndReasons.RequestCancelled : OfferEndReasons.RequestExpired;

    public static void EnsureEndReason(string to, string endReason)
    {
        var allowed = to switch
        {
            RequestStates.Cancelled => RequestEndReasons.Cancelled,
            RequestStates.Expired => RequestEndReasons.Expired,
            _ => throw new ArgumentOutOfRangeException(nameof(to), to, "A request ends only as Cancelled or Expired"),
        };
        if (!allowed.Contains(endReason)) throw new ArgumentOutOfRangeException(nameof(endReason), endReason, $"Not a reason for {to}");
    }

    // Contract 10.3: the traveller hears about an end he did not cause himself.
    public static TaxiNoticeKind? TravellerNoticeFor(string endReason) => endReason switch
    {
        RequestEndReasons.AllDriversBusy or RequestEndReasons.NoReadyDriver or RequestEndReasons.NotBooked or RequestEndReasons.AdminCancelled
            => TaxiNoticeKind.RequestEnded,
        RequestEndReasons.DriverLeftTrip => TaxiNoticeKind.DriverWithdrew,
        _ => null,
    };

    private static readonly string[] OfferFinal =
        [OfferStates.Declined, OfferStates.Missed, OfferStates.NotSelected, OfferStates.NotAgreed, OfferStates.Withdrawn, OfferStates.Closed];

    private static string OfferStamps(string to)
    {
        var parts = new List<string>();
        if (to is OfferStates.Accepted or OfferStates.Declined) parts.Add("\"RespondedAt\" = now()");
        if (to == OfferStates.Selected)
        {
            parts.Add("\"SelectedAt\" = now()");
            parts.Add($"\"ConfirmBy\" = now() + interval '{TaxiDispatchRules.ConfirmSeconds} seconds'");
        }
        if (OfferFinal.Contains(to)) parts.Add("\"ClosedAt\" = now()");
        return parts.Count == 0 ? "" : ", " + string.Join(", ", parts);
    }

    private static string Checked(string kind, IReadOnlyCollection<string> from, string to)
    {
        if (from.Count == 0) throw new ArgumentException("At least one from state is needed", nameof(from));
        foreach (var f in from)
            if (!TaxiRequestTransitions.CanMove(kind, f, to))
                throw new ArgumentException($"{kind} cannot move from {f} to {to}", nameof(from));
        return List(from.Distinct());
    }

    private static string List(IEnumerable<string> values) => string.Join(", ", values.Select(v => $"'{v}'"));
}
