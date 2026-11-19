using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using RentNearBy.Core.Entities.TaxiRequests;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.TaxiRequests.Matching;

namespace RentNearBy.Infrastructure.Services.TaxiRequests;

// MASTER_PLAN B2-8: what a removed listing, a driver going offline, a deleted traveller account, a deleted trip or a block
// does to taxi requests. Every method runs inside the caller's transaction and never commits or calls SaveChanges; the
// caller saves the push rows it added. Requests are locked one by one in id order, each before its offers and bookings (2.4).
public sealed class TaxiRequestClosure(
    TaxiRequestStore store,
    ITaxiPushWriter pushes,
    IDueActionScheduler scheduler,
    TaxiPickupPlaces places,
    ILogger<TaxiRequestClosure> logger) : ITaxiRequestClosure
{
    private static readonly string[] RingingOnly = [OfferStates.Ringing];
    private static readonly string[] AcceptedOnly = [OfferStates.Accepted];
    private static readonly string[] SelectedOnly = [OfferStates.Selected];
    private static readonly string[] ConfirmedOnly = [OfferStates.Confirmed];
    private static readonly string[] BookedOnly = [BookingStates.Booked];
    private static readonly string[] RingingOrAccepted = [OfferStates.Ringing, OfferStates.Accepted];

    public static readonly string RouteOffersOfDriverSql = $"""
        SELECT o."Id", o."RequestId", r."MatchToCityKey", r."MatchFromCityKey" FROM "TaxiRequestOffers" o
        JOIN "TaxiRequests" r ON r."Id" = o."RequestId"
        WHERE o."DriverListingId" = @id AND o."Status" IN ({List(RingingOrAccepted)})
        """;

    public static readonly string OffersOfDriverSql = $"""
        SELECT "Id", "RequestId", "DriverListingId" FROM "TaxiRequestOffers"
        WHERE "DriverListingId" = @id AND "Status" IN ({List(OfferStates.Live)})
        """;

    public static readonly string RingingOffersOfDriverSql = $"""
        SELECT "Id", "RequestId", "DriverListingId" FROM "TaxiRequestOffers"
        WHERE "DriverListingId" = @id AND "Status" = '{OfferStates.Ringing}'
        """;

    public static readonly string BookingsOfDriverSql = $"""
        SELECT "Id", "RequestId", "DriverListingId" FROM "TaxiBookings"
        WHERE "DriverListingId" = @id AND "Status" IN ({BookingStates.ActiveSqlList})
        """;

    public static readonly string OffersBetweenUsersSql = $"""
        SELECT o."Id", o."RequestId", o."DriverListingId" FROM "TaxiRequestOffers" o
        JOIN "TaxiRequests" r ON r."Id" = o."RequestId"
        WHERE o."Status" IN ({List(OfferStates.Live)})
          AND ((o."DriverUserId" = @a AND r."TravellerUserId" = @b) OR (o."DriverUserId" = @b AND r."TravellerUserId" = @a))
        """;

    public static readonly string ActiveRequestsOfTravellerSql = $"""
        SELECT "Id" AS "Value" FROM "TaxiRequests" WHERE "TravellerUserId" = @id AND "Status" IN ({RequestStates.ActiveSqlList}) ORDER BY "Id"
        """;

    public static readonly string ActiveRequestsOfTripSql = $"""
        SELECT "Id" AS "Value" FROM "TaxiRequests" WHERE "TripId" = @id AND "Status" IN ({RequestStates.ActiveSqlList}) ORDER BY "Id"
        """;

    // Ringing, Accepted and Selected offers close (ListingRemoved); an active booking is cancelled by the system with P10
    // (before the trip the request reopens with 3 more accepts, during the trip it ends as DriverLeftTrip); the traveller is told.
    public Task<TaxiClosureResult> ListingRemovedAsync(ApplicationDbContext db, Guid driverListingId, CancellationToken ct = default)
        => CloseListingAsync(db, driverListingId, cancelBookings: true, ct);

    // Admin unpublish or report takedown (Approved to Rejected): the offers close as on a removal, an active booking stays.
    public Task<TaxiClosureResult> ListingTakenDownAsync(ApplicationDbContext db, Guid driverListingId, CancellationToken ct = default)
        => CloseListingAsync(db, driverListingId, cancelBookings: false, ct);

    private async Task<TaxiClosureResult> CloseListingAsync(ApplicationDbContext db, Guid driverListingId, bool cancelBookings, CancellationToken ct)
    {
        var offers = await RowsAsync(db, OffersOfDriverSql, ct, Uuid("id", driverListingId));
        var bookings = cancelBookings ? await RowsAsync(db, BookingsOfDriverSql, ct, Uuid("id", driverListingId)) : [];
        var acc = new Accumulator();
        foreach (var requestId in RequestOrder(offers, bookings))
        {
            var request = await store.LockRequestAsync(db, requestId, ct);
            if (request is null) continue;
            foreach (var o in offers.Where(o => o.RequestId == requestId))
                if (await CloseOfferAsync(db, request, o.Id, OfferStates.Live, OfferEndReasons.ListingRemoved, tellTraveller: true, ct))
                    acc.Touch(request);
            foreach (var b in bookings.Where(b => b.RequestId == requestId))
                await CancelBookingForRemovedListingAsync(db, request, b.Id, acc, ct);
        }
        return await acc.BuildAsync(db, ct);
    }

    // Only ringing offers close (DriverOffline, the ring end says "You are offline"); accepted offers and bookings stay.
    public async Task<TaxiClosureResult> DriverOfflineAsync(ApplicationDbContext db, Guid driverListingId, CancellationToken ct = default)
    {
        var offers = await RowsAsync(db, RingingOffersOfDriverSql, ct, Uuid("id", driverListingId));
        var acc = new Accumulator();
        foreach (var requestId in RequestOrder(offers, []))
        {
            var request = await store.LockRequestAsync(db, requestId, ct);
            if (request is null) continue;
            foreach (var o in offers.Where(o => o.RequestId == requestId))
                if (await CloseOfferAsync(db, request, o.Id, RingingOnly, OfferEndReasons.DriverOffline, tellTraveller: false, ct))
                    acc.Touch(request);
        }
        return await acc.BuildAsync(db, ct);
    }

    // O12: after a route save the driver's ringing and accepted offers that no route of his serves any more close
    // (RouteRemoved; a ringing one gets the ring end push and never counts as a missed ring). Selected and confirmed offers
    // and bookings stay: the driver made a promise. routes = his routes as they will be saved (keys already resolved).
    // The driver ring lock is taken after the request locks (the select order) and the offers are read again under it, so
    // a ring a matcher pass inserted for the old routes is seen (MatcherSql.InsertOffer re-checks the routes after commit).
    public async Task<TaxiClosureResult> RoutesChangedAsync(ApplicationDbContext db, Guid driverListingId,
        IReadOnlyList<RouteServeKeys> routes, CancellationToken ct = default)
    {
        var locked = new Dictionary<Guid, TaxiRequest?>();
        await LockRequestsAsync(db, Gone(await RouteOffersAsync(db, driverListingId, ct), routes), locked, ct);
        await db.Database.ExecuteSqlRawAsync(MatcherSql.LockDriverRing, [Uuid("driver", driverListingId)], ct);
        var gone = Gone(await RouteOffersAsync(db, driverListingId, ct), routes);
        await LockRequestsAsync(db, gone, locked, ct);

        var acc = new Accumulator();
        foreach (var o in gone.OrderBy(o => o.RequestId))
        {
            if (locked[o.RequestId] is not { } request) continue;
            if (await CloseOfferAsync(db, request, o.Id, RingingOrAccepted, OfferEndReasons.RouteRemoved, tellTraveller: false, ct))
            {
                acc.Touch(request);
                acc.Freed.Add(driverListingId);
            }
        }
        return await acc.BuildAsync(db, ct);
    }

    private static async Task<List<RouteOfferRow>> RouteOffersAsync(ApplicationDbContext db, Guid driverListingId, CancellationToken ct)
        => await db.Database.SqlQueryRaw<RouteOfferRow>(RouteOffersOfDriverSql, Uuid("id", driverListingId)).ToListAsync(ct);

    private static List<RouteOfferRow> Gone(IEnumerable<RouteOfferRow> rows, IReadOnlyList<RouteServeKeys> routes)
        => rows.Where(o => !TaxiDispatchRules.RoutesStillServe(o.MatchToCityKey, o.MatchFromCityKey, routes)).ToList();

    private async Task LockRequestsAsync(ApplicationDbContext db, IEnumerable<RouteOfferRow> rows, Dictionary<Guid, TaxiRequest?> locked, CancellationToken ct)
    {
        foreach (var requestId in rows.Select(o => o.RequestId).Where(id => !locked.ContainsKey(id)).Distinct().Order())
            locked[requestId] = await store.LockRequestAsync(db, requestId, ct);
    }

    // Back online: nothing to change in rows; the matcher looks at every city of the driver again.
    public static TaxiClosureResult DriverOnline(Guid driverListingId) => new([], [driverListingId], []);

    // Every active request of the traveller is cancelled (AccountClosed); drivers are told through EndRequestAsync.
    public Task<TaxiClosureResult> TravellerAccountDeletedAsync(ApplicationDbContext db, Guid travellerUserId, CancellationToken ct = default)
        => EndAllAsync(db, ActiveRequestsOfTravellerSql, travellerUserId, RequestEndReasons.AccountClosed, BookingCancelledBy.System, ct);

    // The trip delete route refuses while a request is active (B2-7); this keeps the rows consistent if one still slips through.
    public Task<TaxiClosureResult> TripDeletedAsync(ApplicationDbContext db, Guid tripId, CancellationToken ct = default)
        => EndAllAsync(db, ActiveRequestsOfTripSql, tripId, RequestEndReasons.TravellerCancelled, BookingCancelledBy.Traveller, ct);

    // Live offers between the two people close quietly (no reason in the contract, so no ring end text and no notice);
    // an active booking is left for the two people to cancel.
    public async Task<TaxiClosureResult> UsersBlockedAsync(ApplicationDbContext db, Guid userAId, Guid userBId, CancellationToken ct = default)
    {
        var offers = await RowsAsync(db, OffersBetweenUsersSql, ct, Uuid("a", userAId), Uuid("b", userBId));
        var acc = new Accumulator();
        foreach (var requestId in RequestOrder(offers, []))
        {
            var request = await store.LockRequestAsync(db, requestId, ct);
            if (request is null) continue;
            foreach (var o in offers.Where(o => o.RequestId == requestId))
                if (await CloseOfferAsync(db, request, o.Id, OfferStates.Live, null, tellTraveller: false, ct))
                {
                    acc.Touch(request);
                    acc.Freed.Add(o.DriverListingId);
                }
        }
        return await acc.BuildAsync(db, ct);
    }

    // After commit only; never throws. poolChanged = the pickup lists may have moved (routes, availability, removal).
    public async Task AfterCommitAsync(TaxiClosureResult result, bool poolChanged)
    {
        if (result.CityKeys.Count > 0 || result.FreedDriverListingIds.Count > 0 || result.Changes.Count > 0)
            await store.AfterCommitAsync(result);
        if (!poolChanged) return;
        try
        {
            await places.BumpAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "taxi.request.after_commit failed: {Step}", "pickup bump");
        }
    }

    // One result for several closures of one unit; the newest version of a request wins.
    public static TaxiClosureResult Merge(IEnumerable<TaxiClosureResult> results)
    {
        var list = results.ToList();
        if (list.Count == 0) return TaxiClosureResult.Empty;
        return new TaxiClosureResult(
            list.SelectMany(r => r.CityKeys).Distinct().ToList(),
            list.SelectMany(r => r.FreedDriverListingIds).Distinct().ToList(),
            list.SelectMany(r => r.Changes).GroupBy(c => c.RequestId).Select(g => g.MaxBy(c => c.Version)!).ToList());
    }

    private async Task<TaxiClosureResult> EndAllAsync(ApplicationDbContext db, string sql, Guid id, string endReason, string cancelledBy, CancellationToken ct)
    {
        var requestIds = await db.Database.SqlQueryRaw<Guid>(sql, Uuid("id", id)).ToListAsync(ct);
        var ends = new List<TaxiClosureResult>();
        foreach (var requestId in requestIds)
            if (await store.EndRequestAsync(db, requestId, RequestStates.Cancelled, endReason, null, cancelledBy, ct) is { } end)
                ends.Add(end.Signals);
        return Merge(ends);
    }

    // Caller holds the request lock; the offer is read again after it. Accepted gone and none left: Collecting goes back
    // to Open. Selected gone: the selection lapses (confirm timer stops, request reopens, traveller told when asked to).
    private async Task<bool> CloseOfferAsync(ApplicationDbContext db, TaxiRequest request, Guid offerId, IReadOnlyCollection<string> allowed,
        string? endReason, bool tellTraveller, CancellationToken ct)
    {
        var offer = await TaxiOfferRows.FindAsync(db, offerId, ct);
        if (offer is null || !allowed.Contains(offer.Status)) return false;
        switch (offer.Status)
        {
            case OfferStates.Ringing:
                if (await store.TryMoveOfferAsync(db, offerId, RingingOnly, OfferStates.Closed, endReason, ct: ct) is null) return false;
                await scheduler.CancelAsync(db, DueActionKinds.OfferExpire, offerId, ct);
                if (endReason is not null)
                    await pushes.RingEndAsync(db, new RingEndPush(offerId, request.Id, offer.DriverUserId, endReason), ct);
                return true;
            case OfferStates.Accepted:
                if (await store.TryMoveOfferAsync(db, offerId, AcceptedOnly, OfferStates.Closed, endReason, ct: ct) is null) return false;
                var counts = await store.GetLiveCountsAsync(db, request.Id, ct);
                var current = await TaxiOfferRows.ChangeOfAsync(db, request.Id, ct);
                if (counts.Accepted == 0 && current?.State == RequestStates.Collecting)
                    await store.ReopenRequestAsync(db, request.Id, 0, ct);
                return true;
            case OfferStates.Selected:
                if (await store.TryMoveOfferAsync(db, offerId, SelectedOnly, OfferStates.Closed, endReason, ct: ct) is null) return false;
                await scheduler.CancelAsync(db, DueActionKinds.ConfirmExpire, offerId, ct);
                var reopened = await store.ReopenRequestAsync(db, request.Id, 0, ct);
                if (reopened is not null && tellTraveller)
                    await pushes.NotifyAsync(db, new TaxiNotice(TaxiNoticeKind.SelectionLapsed, request.TravellerUserId, request.TripId, request.Id, offerId), ct);
                return true;
            default:
                return false;
        }
    }

    // P10 with the system as the canceller: the confirmed offer is withdrawn (ListingRemoved), then before the trip the
    // booking is cancelled and the request reopens with 3 more accepts and a fresh stale timer; during the trip it ends.
    private async Task CancelBookingForRemovedListingAsync(ApplicationDbContext db, TaxiRequest request, Guid bookingId, Accumulator acc, CancellationToken ct)
    {
        var booking = await db.Set<TaxiBooking>().AsNoTracking()
            .Where(b => b.Id == bookingId && BookingStates.Active.Contains(b.Status))
            .Select(b => new { b.Id, b.Status, b.OfferId })
            .FirstOrDefaultAsync(ct);
        if (booking is null) return;
        await store.TryMoveOfferAsync(db, booking.OfferId, ConfirmedOnly, OfferStates.Withdrawn, OfferEndReasons.ListingRemoved, ct: ct);

        if (booking.Status == BookingStates.InProgress)
        {
            var end = await store.EndRequestAsync(db, request.Id, RequestStates.Cancelled, RequestEndReasons.DriverLeftTrip, null, BookingCancelledBy.System, ct);
            if (end is not null) acc.Add(end.Signals);
            return;
        }

        if (await store.TryMoveBookingAsync(db, booking.Id, BookedOnly, BookingStates.Cancelled, BookingCancelledBy.System, ct: ct) is null) return;
        await scheduler.CancelAsync(db, DueActionKinds.BookingStart, booking.Id, ct);
        await scheduler.CancelAsync(db, DueActionKinds.BookingAutoComplete, booking.Id, ct);
        var reopened = await store.ReopenRequestAsync(db, request.Id, TaxiDispatchRules.AcceptBatch, ct);
        if (reopened is not null)
            await scheduler.AddAtAsync(db, DueActionKinds.RequestStaleExpire, request.Id, request.Id, (int)reopened.Version,
                TaxiDispatchRules.StaleDueAt(request.DateFrom), ct);
        await pushes.NotifyAsync(db, new TaxiNotice(TaxiNoticeKind.DriverWithdrew, request.TravellerUserId, request.TripId, request.Id, booking.OfferId), ct);
        acc.Touch(request);
    }

    private static IEnumerable<Guid> RequestOrder(IEnumerable<SubjectRow> offers, IEnumerable<SubjectRow> bookings)
        => offers.Select(o => o.RequestId).Concat(bookings.Select(b => b.RequestId)).Distinct().Order();

    private static async Task<List<SubjectRow>> RowsAsync(ApplicationDbContext db, string sql, CancellationToken ct, params NpgsqlParameter[] parameters)
        => await db.Database.SqlQueryRaw<SubjectRow>(sql, parameters).ToListAsync(ct);

    private static NpgsqlParameter Uuid(string name, Guid value) => new(name, NpgsqlDbType.Uuid) { Value = value };

    private static string List(IEnumerable<string> values) => string.Join(", ", values.Select(v => $"'{v}'"));

    private sealed class Accumulator
    {
        private readonly Dictionary<Guid, long> _requests = new();
        private readonly List<TaxiClosureResult> _ends = new();
        public HashSet<Guid> Freed { get; } = new();

        public void Touch(TaxiRequest request) => _requests[request.Id] = request.DispatchCityKey;

        public void Add(TaxiClosureResult end) => _ends.Add(end);

        // Versions are read after every bump of this unit, so the realtime hint carries the final one.
        public async Task<TaxiClosureResult> BuildAsync(ApplicationDbContext db, CancellationToken ct)
        {
            var changes = new List<TaxiRequestChange>();
            foreach (var id in _requests.Keys)
                if (await TaxiOfferRows.ChangeOfAsync(db, id, ct) is { } change) changes.Add(change);
            var own = new TaxiClosureResult(_requests.Values.Distinct().ToList(), Freed.ToList(), changes);
            return Merge(_ends.Prepend(own));
        }
    }

    private sealed class RouteOfferRow
    {
        public Guid Id { get; set; }
        public Guid RequestId { get; set; }
        public long MatchToCityKey { get; set; }
        public long? MatchFromCityKey { get; set; }
    }

    private sealed class SubjectRow
    {
        public Guid Id { get; set; }
        public Guid RequestId { get; set; }
        public Guid DriverListingId { get; set; }
    }
}
