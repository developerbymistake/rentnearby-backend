using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using RentNearBy.Core.DTOs.TaxiRequests;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Entities.TaxiRequests;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.TaxiRequests.Matching;

namespace RentNearBy.Infrastructure.Services.TaxiRequests;

// Request = the row after the action (a fresh read); Error = the contract error to answer with. Created = a new row (201).
public sealed record TaxiRequestResult(TaxiRequest? Request, TaxiRequestError? Error, bool Created = false, TimeSpan? RetryAfter = null)
{
    public bool IsOk => Error is null;

    public static TaxiRequestResult Ok(TaxiRequest request, bool created = false) => new(request, null, created);

    public static TaxiRequestResult Fail(TaxiRequestError error, TimeSpan? retryAfter = null) => new(null, error, false, retryAfter);

    public static TaxiRequestResult Fail(int status, string type, object? data = null, string? message = null)
        => new(null, TaxiRequestCreateRules.Error(status, type, message, data));
}

// MASTER_PLAN B2-5: the traveller side of a taxi request. Ownership is checked before any change; every request, offer and
// booking transition goes through TaxiRequestStore; pushes are rows in the same transaction; signals only after commit.
public sealed class TaxiRequestService(
    ApplicationDbContext db,
    TaxiRequestStore store,
    TaxiPickupPlaces places,
    IDueActionScheduler scheduler,
    ITaxiPushWriter pushes,
    IRateLimitService rateLimiter,
    ILogger<TaxiRequestService> logger,
    Func<DateTime>? utcNow = null)
{
    public const string RateLimitPrefix = "taxi:request:";
    public static readonly TimeSpan RateWindow = TimeSpan.FromHours(1);
    public const int AdminNoteMaxLength = TaxiRequestCreateRules.AdminNoteMaxLength;
    private const string UniqueViolation = "23505";

    private readonly Func<DateTime> _utcNow = utcNow ?? (() => DateTime.UtcNow);

    public static string RateLimitKey(Guid userId) => RateLimitPrefix + userId.ToString("D");

    // T2. Returns Open at once (dispatch is asynchronous, decision 1); the active request locks the plan.
    public async Task<TaxiRequestResult> CreateAsync(Guid tripId, Guid userId, CreateTaxiRequestDto body, CancellationToken ct = default)
    {
        var trip = await db.Trips.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tripId, ct);
        if (trip is null) return TaxiRequestResult.Fail(404, TripErrorTypes.TripNotFound, message: "Trip not found");
        if (trip.OwnerUserId != userId) return TaxiRequestResult.Fail(403, TaxiRequestErrorTypes.NotTripOwner);

        if (body.ClientRequestId is { } clientId && await FindByClientIdAsync(userId, clientId, ct) is { } replay)
            return ReplayOrConflict(replay, tripId);

        var limit = await rateLimiter.CheckAsync(RateLimitKey(userId), TaxiDispatchRules.CreatesPerHour, RateWindow);
        if (!limit.IsAllowed)
            return TaxiRequestResult.Fail(TaxiRequestCreateRules.Error(429, TaxiRequestErrorTypes.TaxiRequestLimitReached), limit.RetryAfter);

        var result = await CreateCoreAsync(trip, userId, body, ct);
        if (!result.Created && limit.Token is { } token) await ReleaseQuietlyAsync(userId, token);
        return result;
    }

    private async Task<TaxiRequestResult> CreateCoreAsync(Trip trip, Guid userId, CreateTaxiRequestDto body, CancellationToken ct)
    {
        // The pickup lists need the catalog (no call inside a transaction), so they are read first; the trip is checked
        // again under its lock and a plan whose first or last city moved meanwhile is read once more from the start.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var cities = CitiesOf(TripPlanReader.Read(trip), body);
            var lists = await places.GetAsync(cities.First, cities.Last, ct);
            var sightseeingDrivers = cities.Sightseeing == cities.First ? lists.SightseeingDrivers : await places.CountForAsync(cities.Sightseeing, ct);
            if (Check(trip, body, lists, cities, sightseeingDrivers).Error is { } early) return TaxiRequestResult.Fail(early);

            var outcome = await PgRetry.OnceAsync(db, c => InsertUnderTripLockAsync(trip.Id, userId, body, lists, cities, sightseeingDrivers, c), ct, logger);
            if (outcome.Retry is { } fresh)
            {
                trip = fresh;
                continue;
            }
            if (outcome.Result is { Created: true, Request: { } created } ok)
            {
                await store.AfterCommitAsync(new TaxiClosureResult([created.DispatchCityKey], [],
                    [new TaxiRequestChange(created.Id, created.TripId, created.TravellerUserId, created.Version, created.Status)]));
                return ok;
            }
            return outcome.Result!;
        }
        return TaxiRequestResult.Fail(409, TaxiRequestErrorTypes.TaxiRequestStateInvalid);
    }

    private sealed record InsertOutcome(TaxiRequestResult? Result, Trip? Retry);

    // The plan cities a create depends on: first and last (pickup and drop lists) and the sightseeing city (O4).
    public sealed record TaxiCreateCities(long First, long Last, long Sightseeing);

    public static TaxiCreateCities CitiesOf(TripPlan plan, CreateTaxiRequestDto body)
    {
        var (first, last) = EndCities(plan);
        var days = body.Sightseeing?.On == true ? body.Sightseeing.Days : null;
        return new TaxiCreateCities(first, last, TaxiRequestCreateRules.SightseeingCityOf(plan, days, first));
    }

    private async Task<InsertOutcome> InsertUnderTripLockAsync(Guid tripId, Guid userId, CreateTaxiRequestDto body, TaxiPickupLists lists,
        TaxiCreateCities cities, int sightseeingDrivers, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await store.LockTripAsync(db, tripId, ct);
        var trip = await db.Trips.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tripId, ct);
        if (trip is null) return new InsertOutcome(TaxiRequestResult.Fail(404, TripErrorTypes.TripNotFound, message: "Trip not found"), null);
        if (trip.OwnerUserId != userId) return new InsertOutcome(TaxiRequestResult.Fail(403, TaxiRequestErrorTypes.NotTripOwner), null);

        var plan = TripPlanReader.Read(trip);
        if (CitiesOf(plan, body) != cities) return new InsertOutcome(null, trip);

        if (body.ClientRequestId is { } clientId && await FindByClientIdAsync(userId, clientId, ct) is { } replay)
            return new InsertOutcome(ReplayOrConflict(replay, tripId), null);

        var check = Check(trip, body, lists, cities, sightseeingDrivers);
        if (check.Error is { } error) return new InsertOutcome(TaxiRequestResult.Fail(error), null);
        var createPlan = check.Plan!;

        var legacyRows = await db.TripEnquiries.AsNoTracking()
            .Where(e => e.TripId == tripId && e.Kind == TripEnquiryKinds.Taxi && TripEnquiryStatuses.Open.Contains(e.Status))
            .ToListAsync(ct);
        if (TaxiRequestCreateRules.LegacyBlock(legacyRows) is { } legacy)
            return new InsertOutcome(TaxiRequestResult.Fail(TaxiRequestCreateRules.LegacyError(legacy)), null);

        if (await ActiveRequestIdAsync(tripId, ct) is { } activeId)
            return new InsertOutcome(AlreadyActive(activeId), null);

        // O3 + O10: one traveller, one taxi per date. The traveller lock serialises his creates across trips, so two trips
        // racing cannot both pass the overlap check.
        await db.Database.ExecuteSqlRawAsync(TravellerLockSql, [Uuid("traveller", userId)], ct);
        if (await OverlapOfAsync(userId, tripId, createPlan.Occupied, ct) is { } overlap)
            return new InsertOutcome(TaxiRequestResult.Fail(TaxiRequestCreateRules.OverlapError(overlap)), null);

        var pickupName = lists.PickupName(createPlan.PickupCityKey);
        var dropName = createPlan.DropSameAsPickup ? pickupName : lists.DropName(createPlan.DropCityKey);
        var snapshot = TaxiTripSnapshot.Build(plan, createPlan.TripStart, trip.Title, body, pickupName, dropName);

        var request = new TaxiRequest
        {
            Id = Guid.NewGuid(),
            TripId = tripId,
            TravellerUserId = userId,
            Status = RequestStates.Open,
            WantPickup = body.Pickup?.On == true,
            WantSightseeing = body.Sightseeing?.On == true,
            WantDrop = body.Drop?.On == true,
            PickupCityKey = createPlan.PickupCityKey,
            PickupCityName = pickupName,
            DropCityKey = createPlan.DropCityKey,
            DropCityName = dropName,
            DropSameAsPickup = createPlan.DropSameAsPickup,
            DispatchCityKey = createPlan.DispatchCityKey,
            MatchToCityKey = createPlan.MatchToCityKey,
            MatchFromCityKey = createPlan.MatchFromCityKey,
            Seats = (short)createPlan.Seats,
            Travellers = (short)TaxiRequestCreateRules.TravellersOf(plan.Settings),
            DateFrom = createPlan.Occupied.From,
            DateTo = createPlan.Occupied.To,
            SightseeingDaysJson = JsonSerializer.Serialize(createPlan.SightseeingDays),
            TripSnapshotJson = snapshot.ToJson(),
            ClientRequestId = body.ClientRequestId,
        };
        db.Set<TaxiRequest>().Add(request);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Another create of this trip or this client id won the race (the trip lock makes it rare).
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            return new InsertOutcome(await AfterRaceAsync(tripId, userId, body.ClientRequestId, ct), null);
        }

        await scheduler.AddAtAsync(db, DueActionKinds.RequestStaleExpire, request.Id, request.Id, 0, TaxiDispatchRules.StaleDueAt(request.DateFrom), ct);
        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();

        var saved = await LoadAsync(request.Id, ct);
        return new InsertOutcome(TaxiRequestResult.Ok(saved!, created: true), null);
    }

    // T5. Booked needs a chip and ends as BookingCancelledByTraveller; InProgress is ended with T9, not cancelled.
    public async Task<TaxiRequestResult> CancelAsync(Guid requestId, Guid userId, string? reason, string? chip, CancellationToken ct = default)
    {
        if (await OwnedAsync(requestId, userId, ct) is { Error: { } denied }) return TaxiRequestResult.Fail(denied);

        return await RunAsync(requestId, async (locked, c) =>
        {
            var check = TaxiRequestCreateRules.CancelCheck(locked.Status, reason, chip);
            if (check.Error is { } error) return Step.Fail(error);
            if (check.AlreadyDone) return Step.Unchanged;
            var plan = check.Plan!;
            var end = await store.EndRequestAsync(db, requestId, RequestStates.Cancelled, plan.EndReason, plan.Chip, plan.CancelledBy, c);
            return end is null ? Step.StateInvalid(locked) : Step.Changed(end.Signals);
        }, ct);
    }

    // Admin cancel (AD4): same closing path, reason AdminCancelled, the note is kept on the row.
    public async Task<TaxiRequestResult> CancelByAdminAsync(Guid requestId, string note, CancellationToken ct = default)
    {
        var (text, noteError) = TaxiRequestCreateRules.AdminNoteCheck(note);
        if (noteError is not null) return TaxiRequestResult.Fail(noteError);

        return await RunAsync(requestId, async (locked, c) =>
        {
            if (locked.Status == RequestStates.Cancelled && locked.EndReason == RequestEndReasons.AdminCancelled) return Step.Unchanged;
            var end = await store.EndRequestAsync(db, requestId, RequestStates.Cancelled, RequestEndReasons.AdminCancelled, null, BookingCancelledBy.Admin, c);
            if (end is null) return Step.StateInvalid(locked);
            await db.Set<TaxiRequest>().Where(r => r.Id == requestId).ExecuteUpdateAsync(s => s.SetProperty(r => r.AdminNote, text), c);
            return Step.Changed(end.Signals);
        }, ct);
    }

    // T6 (P4): 3 more accepts within the cap of 9 (never below the target), rings drivers not yet offered; on an exhausted
    // request (O5) it is the only way to ring again. Nothing to add = the current object.
    public async Task<TaxiRequestResult> AskMoreAsync(Guid requestId, Guid userId, CancellationToken ct = default)
    {
        if (await OwnedAsync(requestId, userId, ct) is { Error: { } denied }) return TaxiRequestResult.Fail(denied);

        return await RunAsync(requestId, async (locked, c) =>
        {
            if (!RequestStates.Dispatching.Contains(locked.Status)) return Step.StateInvalid(locked);
            if (!TaxiDispatchRules.CanAskMore(locked.Status, locked.AcceptedCount, locked.AcceptTarget, locked.QueueExhaustedAt is not null)) return Step.Unchanged;
            var remaining = await TaxiCandidateQuery.CountRemainingAsync(db, requestId, c);
            if (remaining.Free + remaining.Busy == 0) return Step.Fail(TaxiRequestCreateRules.Error(409, TaxiRequestErrorTypes.AllDriversBusy));

            var rows = await db.Database.SqlQueryRaw<long>(AskMoreSql, Uuid("id", requestId)).ToListAsync(c);
            if (rows.Count == 0) return Step.StateInvalid(locked);
            return Step.Changed(new TaxiClosureResult([locked.DispatchCityKey], [],
                [new TaxiRequestChange(locked.Id, locked.TripId, locked.TravellerUserId, rows[0], locked.Status)]));
        }, ct);
    }

    public static readonly string AskMoreSql = $"""
        UPDATE "TaxiRequests" SET "AcceptTarget" = GREATEST("AcceptTarget", LEAST({TaxiDispatchRules.MaxAccepts}, "AcceptedCount" + {TaxiDispatchRules.AcceptBatch}))::smallint,
            "QueueExhaustedAt" = NULL, "QueueWaitStartedAt" = NULL, "Version" = "Version" + 1, "UpdatedAt" = now()
        WHERE "Id" = @id AND "Status" IN ('{RequestStates.Open}', '{RequestStates.Collecting}')
            AND (("AcceptedCount" + {TaxiDispatchRules.AcceptBatch} > "AcceptTarget" AND "AcceptTarget" < {TaxiDispatchRules.MaxAccepts})
                 OR ("QueueExhaustedAt" IS NOT NULL AND "AcceptedCount" < "AcceptTarget"))
        RETURNING "Version" AS "Value"
        """;

    // T8: the Accepted driver loses the traveller's contact and leaves the list. When no accepted driver is left the
    // request goes back to Open (2.3), so ask more can start a new batch.
    public async Task<TaxiRequestResult> NotAgreedAsync(Guid requestId, Guid offerId, Guid userId, CancellationToken ct = default)
    {
        if (await OwnedAsync(requestId, userId, ct) is { Error: { } denied }) return TaxiRequestResult.Fail(denied);
        if (await OfferOfAsync(requestId, offerId, ct) is null) return TaxiRequestResult.Fail(404, TaxiRequestErrorTypes.OfferNotFound);

        return await RunAsync(requestId, async (locked, c) =>
        {
            var offer = await OfferOfAsync(requestId, offerId, c);
            if (offer is null) return Step.Fail(TaxiRequestCreateRules.Error(404, TaxiRequestErrorTypes.OfferNotFound));
            if (offer.Status == OfferStates.NotAgreed) return Step.Unchanged;
            if (offer.Status != OfferStates.Accepted) return Step.OfferInvalid(offer.Status);

            var moved = await store.TryMoveOfferAsync(db, offerId, [OfferStates.Accepted], OfferStates.NotAgreed, null, null, c);
            if (moved is null) return Step.OfferInvalid(offer.Status);

            var change = new TaxiRequestChange(locked.Id, locked.TripId, locked.TravellerUserId, moved.Version, locked.Status);
            if (locked.Status == RequestStates.Collecting && await store.ReopenRequestAsync(db, requestId, 0, c) is { } reopened)
                change = reopened.Change;
            return Step.Changed(new TaxiClosureResult([locked.DispatchCityKey], [], [change]));
        }, ct);
    }

    // T7: Collecting + an Accepted offer -> Selected, the driver has 15 minutes (ConfirmBy and ConfirmExpire on the database clock).
    public async Task<TaxiRequestResult> SelectAsync(Guid requestId, Guid offerId, Guid userId, CancellationToken ct = default)
    {
        if (await OwnedAsync(requestId, userId, ct) is { Error: { } denied }) return TaxiRequestResult.Fail(denied);
        if (await OfferOfAsync(requestId, offerId, ct) is null) return TaxiRequestResult.Fail(404, TaxiRequestErrorTypes.OfferNotFound);

        return await RunAsync(requestId, async (locked, c) =>
        {
            if (locked.Status == RequestStates.Selected && locked.SelectedOfferId == offerId) return Step.Unchanged;
            if (locked.Status != RequestStates.Collecting) return Step.StateInvalid(locked);
            var offer = await OfferOfAsync(requestId, offerId, c);
            if (offer is null) return Step.Fail(TaxiRequestCreateRules.Error(404, TaxiRequestErrorTypes.OfferNotFound));
            if (offer.Status != OfferStates.Accepted) return Step.OfferInvalid(offer.Status);

            // The driver's taxi was taken down, sent back to review or deleted after he accepted, or he was booked for
            // overlapping dates by another trip meanwhile: the offer closes, nothing is selected.
            var closeReason = !await TaxiOfferRows.IsDriverBookableAsync(db, offer.DriverListingId, c) ? OfferEndReasons.ListingRemoved
                : await DriverCalendar.ConflictForAsync(db, offer.DriverListingId, locked.DateFrom, locked.DateTo, c) is not null ? OfferEndReasons.Booked
                : null;
            if (closeReason is not null)
            {
                var closed = await store.TryMoveOfferAsync(db, offerId, [OfferStates.Accepted], OfferStates.Closed, closeReason, null, c);
                if (closed is null) return Step.OfferInvalid(offer.Status);
                var change = new TaxiRequestChange(locked.Id, locked.TripId, locked.TravellerUserId, closed.Version, locked.Status);
                if (await store.ReopenRequestAsync(db, requestId, 0, c) is { } reopened) change = reopened.Change;
                return Step.OfferClosed(new TaxiClosureResult([locked.DispatchCityKey], [offer.DriverListingId], [change]));
            }

            // After the request row (2.4): a matcher pass holding this driver's ring lock finishes its insert first.
            await db.Database.ExecuteSqlRawAsync(MatcherSql.LockDriverRing, [Uuid("driver", offer.DriverListingId)], c);
            var movedOffer = await store.TryMoveOfferAsync(db, offerId, [OfferStates.Accepted], OfferStates.Selected, null, null, c);
            if (movedOffer is null) return Step.OfferInvalid(offer.Status);
            var movedRequest = await store.TryMoveRequestAsync(db, requestId, [RequestStates.Collecting], RequestStates.Selected, null, null, c);
            if (movedRequest is null) return Step.StateInvalid(locked);

            await db.Database.ExecuteSqlRawAsync(SetSelectedOfferSql, [Uuid("id", requestId), Uuid("offerId", offerId)], c);
            await scheduler.AddAsync(db, DueActionKinds.ConfirmExpire, offerId, requestId, 0, TimeSpan.FromSeconds(TaxiDispatchRules.ConfirmSeconds), c);
            await ChooseReminders.CancelAsync(scheduler, db, requestId, c);
            await pushes.NotifyAsync(db, new TaxiNotice(TaxiNoticeKind.Selected, movedOffer.DriverUserId, locked.TripId, requestId, offerId), c);
            return Step.Changed(new TaxiClosureResult([locked.DispatchCityKey], [], [movedRequest.Change]));
        }, ct);
    }

    public static readonly string SetSelectedOfferSql = $"""
        UPDATE "TaxiRequests" SET "SelectedOfferId" = @offerId WHERE "Id" = @id AND "Status" = '{RequestStates.Selected}'
        """;

    public Task<TaxiRequest?> LoadAsync(Guid requestId, CancellationToken ct = default)
        => db.Set<TaxiRequest>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == requestId, ct);

    // Contract 5: an unknown id is 404, another traveller's request is 403. Checked before any change.
    public async Task<TaxiRequestResult> OwnedAsync(Guid requestId, Guid userId, CancellationToken ct = default)
    {
        var request = await LoadAsync(requestId, ct);
        if (request is null) return TaxiRequestResult.Fail(404, TaxiRequestErrorTypes.TaxiRequestNotFound);
        return request.TravellerUserId == userId ? TaxiRequestResult.Ok(request) : TaxiRequestResult.Fail(403, TaxiRequestErrorTypes.NotTripOwner);
    }

    private sealed record Step(TaxiRequestError? Error, TaxiClosureResult? Signals)
    {
        public static readonly Step Unchanged = new(null, null);
        public static Step Changed(TaxiClosureResult signals) => new(null, signals);
        public static Step Fail(TaxiRequestError error) => new(error, null);
        public static Step StateInvalid(TaxiRequest r) => new(TaxiRequestCreateRules.Error(409, TaxiRequestErrorTypes.TaxiRequestStateInvalid,
            data: new TaxiRequestStateInvalidData { State = r.Status, Version = r.Version }), null);
        public static Step OfferInvalid(string offerState) => new(TaxiRequestCreateRules.Error(409, TaxiRequestErrorTypes.OfferStateInvalid,
            data: new OfferStateData { State = offerState }), null);
        // Refused, but the offer close is committed and signalled.
        public static Step OfferClosed(TaxiClosureResult signals) => new(TaxiRequestCreateRules.Error(409, TaxiRequestErrorTypes.OfferStateInvalid,
            data: new OfferStateData { State = OfferStates.Closed }), signals);
    }

    // One unit: READ COMMITTED transaction, request row FOR UPDATE (lock order 2.4), the step, commit, fresh read, signals.
    // An error or an unchanged step rolls back and writes nothing.
    private async Task<TaxiRequestResult> RunAsync(Guid requestId, Func<TaxiRequest, CancellationToken, Task<Step>> step, CancellationToken ct)
    {
        var outcome = await PgRetry.OnceAsync(db, async c =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, c);
            var locked = await store.LockRequestAsync(db, requestId, c);
            if (locked is null) return new Step(TaxiRequestCreateRules.Error(404, TaxiRequestErrorTypes.TaxiRequestNotFound), null);
            var result = await step(locked, c);
            if (result.Signals is null) return result;
            await db.SaveChangesAsync(c);
            await tx.CommitAsync(c);
            return result;
        }, ct, logger);
        db.ChangeTracker.Clear();

        if (outcome.Signals is { } signals) await store.AfterCommitAsync(signals);
        if (outcome.Error is { } error) return TaxiRequestResult.Fail(error);
        var fresh = await LoadAsync(requestId, ct);
        return fresh is null ? TaxiRequestResult.Fail(404, TaxiRequestErrorTypes.TaxiRequestNotFound) : TaxiRequestResult.Ok(fresh);
    }

    private TaxiCreateCheck Check(Trip trip, CreateTaxiRequestDto body, TaxiPickupLists lists, TaxiCreateCities cities, int sightseeingDrivers)
    {
        var plan = TripPlanReader.Read(trip);
        var facts = new TaxiCreateFacts(
            plan.Settings?.StartDate,
            plan.Days?.Count(d => d != null) ?? 0,
            trip.Status == TripStatuses.Completed,
            IstTime.TodayIst(_utcNow()),
            cities.First,
            cities.Last,
            lists.PickupKeys,
            lists.DropKeys,
            sightseeingDrivers,
            cities.Sightseeing,
            TaxiRequestCreateRules.TravellersOf(plan.Settings));
        return TaxiRequestCreateRules.Check(body, facts);
    }

    public const string TravellerLockSql = "SELECT pg_advisory_xact_lock(hashtextextended('taxi-traveller:' || @traveller::text, 0))";

    // Another trip of the traveller holding an active request or an active booking on overlapping dates (inclusive).
    private async Task<TaxiOverlap?> OverlapOfAsync(Guid userId, Guid tripId, TaxiDateRange range, CancellationToken ct)
    {
        var request = await (
                from r in db.Set<TaxiRequest>().AsNoTracking()
                join t in db.Trips.AsNoTracking() on r.TripId equals t.Id into trips
                from t in trips.DefaultIfEmpty()
                where r.TravellerUserId == userId && r.TripId != tripId && RequestStates.Active.Contains(r.Status)
                      && r.DateFrom <= range.To && range.From <= r.DateTo
                orderby r.DateFrom
                select new TaxiOverlap(r.TripId, t != null ? t.Title : string.Empty, r.Id, r.DateFrom, r.DateTo))
            .FirstOrDefaultAsync(ct);
        if (request is not null) return request;

        return await (
                from b in db.Set<TaxiBooking>().AsNoTracking()
                join t in db.Trips.AsNoTracking() on b.TripId equals t.Id into trips
                from t in trips.DefaultIfEmpty()
                where b.TravellerUserId == userId && b.TripId != tripId && BookingStates.Active.Contains(b.Status)
                      && b.DateFrom <= range.To && range.From <= b.DateTo
                orderby b.DateFrom
                select new TaxiOverlap(b.TripId, t != null ? t.Title : b.TripName, b.RequestId, b.DateFrom, b.DateTo))
            .FirstOrDefaultAsync(ct);
    }

    // First city = leg of day 1, last city = leg of the last day (a plan without days uses its legs).
    public static (long First, long Last) EndCities(TripPlan plan)
    {
        var days = plan.Days?.Where(d => d != null).Select(d => d.Day).ToList() ?? [];
        if (days.Count == 0 || plan.Legs is not { Count: > 0 })
            return (plan.Legs?.FirstOrDefault()?.CityKey ?? plan.CityKey, plan.Legs?.LastOrDefault()?.CityKey ?? plan.CityKey);
        return (plan.LegOf(days.Min())?.CityKey ?? plan.CityKey, plan.LegOf(days.Max())?.CityKey ?? plan.CityKey);
    }

    public const string ClientIdReusedMessage = "Invalid request id";

    // A client id already used for another trip is a client error, whatever state that other request is in.
    public static TaxiRequestResult ReplayOrConflict(TaxiRequest existing, Guid tripId)
        => existing.TripId == tripId ? TaxiRequestResult.Ok(existing)
            : TaxiRequestResult.Fail(400, TaxiRequestCreateRules.BadRequestType, message: ClientIdReusedMessage);

    private static TaxiRequestResult AlreadyActive(Guid requestId)
        => TaxiRequestResult.Fail(409, TaxiRequestErrorTypes.TaxiRequestAlreadyActive, new TaxiRequestAlreadyActiveData { RequestId = requestId });

    private async Task<TaxiRequestResult> AfterRaceAsync(Guid tripId, Guid userId, Guid? clientId, CancellationToken ct)
    {
        if (clientId is { } id && await FindByClientIdAsync(userId, id, ct) is { } replay) return ReplayOrConflict(replay, tripId);
        if (await ActiveRequestIdAsync(tripId, ct) is { } active) return AlreadyActive(active);
        return TaxiRequestResult.Fail(409, TaxiRequestErrorTypes.TaxiRequestStateInvalid);
    }

    private Task<TaxiRequest?> FindByClientIdAsync(Guid userId, Guid clientId, CancellationToken ct)
        => db.Set<TaxiRequest>().AsNoTracking().FirstOrDefaultAsync(r => r.TravellerUserId == userId && r.ClientRequestId == clientId, ct);

    private async Task<Guid?> ActiveRequestIdAsync(Guid tripId, CancellationToken ct)
    {
        var ids = await db.Set<TaxiRequest>().AsNoTracking()
            .Where(r => r.TripId == tripId && RequestStates.Active.Contains(r.Status))
            .Select(r => r.Id).Take(1).ToListAsync(ct);
        return ids.Count == 0 ? null : ids[0];
    }

    private Task<TaxiRequestOffer?> OfferOfAsync(Guid requestId, Guid offerId, CancellationToken ct)
        => db.Set<TaxiRequestOffer>().AsNoTracking().FirstOrDefaultAsync(o => o.Id == offerId && o.RequestId == requestId, ct);

    private async Task ReleaseQuietlyAsync(Guid userId, string token)
    {
        try
        {
            await rateLimiter.ReleaseAsync(RateLimitKey(userId), token);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Releasing the taxi request create permit failed");
        }
    }

    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        for (var e = (Exception?)ex; e is not null; e = e.InnerException)
            if (e is PostgresException { SqlState: UniqueViolation }) return true;
        return false;
    }

    private static NpgsqlParameter Uuid(string name, Guid value) => new(name, NpgsqlDbType.Uuid) { Value = value };
}
