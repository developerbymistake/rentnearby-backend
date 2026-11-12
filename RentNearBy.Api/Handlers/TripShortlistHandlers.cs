using System.Security.Claims;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

// Server-owned shortlist of the guided flow (TripShortlistItems): max 3 Active per trip step, 72 h clock from AddedAt.
public static class TripShortlistHandlers
{
    private const int NameMax = 120;

    private static IResult TripNotFound() => ErrorResponse(404, "Trip not found", TripErrorTypes.TripNotFound);

    private static IResult BadStep() => BadRequestResponse("Invalid step", TripEnquiryErrorTypes.InvalidStepKey);

    private static async Task<(Trip? Trip, IResult? Error)> LoadTripAsync(Guid tripId, Guid callerId, IUnitOfWork unitOfWork, ApplicationDbContext db)
    {
        var trip = await unitOfWork.Trips.GetByIdForOwnerAsync(tripId, callerId);
        return trip != null ? (trip, null) : (null, TripNotFound());
    }

    public static ShortlistItemDto ToDto(TripShortlistItem x) => new()
    {
        StepKey = x.StepKey,
        Kind = x.Kind,
        ListingId = x.ListingId,
        Name = x.ListingName,
        JourneyId = x.JourneyId,
        LegIndex = x.LegIndex,
        State = x.State,
        ClosedReason = x.ClosedReason,
        AddedAt = x.AddedAt,
        ClosesAt = x.ClosesAt,
    };

    // A fresh Active row: new clock, no earlier reminder or final push.
    public static void Reopen(TripShortlistItem row, DateTime now)
    {
        row.State = TripShortlistStates.Active;
        row.AddedAt = now;
        row.ClosesAt = now.AddHours(TripExecutionRules.ShortlistHours);
        row.ClosedReason = null;
        row.ClosedAt = null;
        row.RemindedOn = null;
        row.FinalPushedAt = null;
    }

    // GET /trips/{tripId}/shortlist
    public static async Task<IResult> Get(Guid tripId, ClaimsPrincipal principal, IUnitOfWork unitOfWork, ApplicationDbContext db)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var callerId)) return UnauthorizedResponse();
        var (trip, error) = await LoadTripAsync(tripId, callerId, unitOfWork, db);
        if (error != null) return error;

        var items = await unitOfWork.TripShortlist.GetForTripAsync(trip!.Id, DateTime.UtcNow);
        return OkResponse(new TripShortlistDto
        {
            Max = TripExecutionRules.MaxShortlistPerStep,
            Items = items.Select(ToDto).ToList(),
        });
    }

    // PUT /trips/{tripId}/steps/{stepKey}/shortlist/{listingId}
    public static async Task<IResult> Add(
        Guid tripId, string stepKey, Guid listingId, AddShortlistRequest request, IValidator<AddShortlistRequest> validator,
        ClaimsPrincipal principal, IUnitOfWork unitOfWork, ApplicationDbContext db)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var callerId)) return UnauthorizedResponse();
        if (!TripStepKeys.IsValid(stepKey)) return BadStep();
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);
        if (listingId == Guid.Empty) return BadRequestResponse("Invalid listing");

        var (trip, error) = await LoadTripAsync(tripId, callerId, unitOfWork, db);
        if (error != null) return error;
        if (trip!.Status == TripStatuses.Completed)
            return ConflictResponse("This trip is complete. Plan changes are off.", TripEnquiryErrorTypes.TripCompleted);

        // The step must be in the plan and fit the kind (stay under stay:k, taxi under taxi, car under a travel step).
        if (TripGuards.StepMismatch(TripPlanReader.Read(trip), stepKey, request.Kind) is { } mismatch)
            return TripGuards.StepMismatchError(mismatch);

        // The listing is read from the server: it must exist as this kind, be open for asks and not be the caller's own.
        var listing = await LoadListingAsync(db, request.Kind, listingId);
        if (listing == null)
            return ErrorResponse(404, "This listing was not found.", TripEnquiryErrorTypes.ListingNotAvailable);
        if (!listing.Available || listing.OwnerUserId == callerId)
            return ConflictResponse("This one is not available any more.", TripEnquiryErrorTypes.ListingNotAvailable);

        var now = DateTime.UtcNow;
        await unitOfWork.BeginTransactionAsync();
        try
        {
            // The step cap is counted under the trip row lock, so two taps cannot both take the last place.
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Trips\" WHERE \"Id\" = {trip.Id} FOR UPDATE");

            var existing = await unitOfWork.TripShortlist.FindActiveAsync(trip.Id, stepKey, listingId);
            if (existing == null)
            {
                if (await unitOfWork.TripShortlist.CountActiveAsync(trip.Id, stepKey) >= TripExecutionRules.MaxShortlistPerStep)
                {
                    await unitOfWork.RollbackTransactionAsync();
                    return ConflictResponse("Your shortlist is full. Remove one to add another.", TripEnquiryErrorTypes.ShortlistFull);
                }

                // A host already asked in this step is not added again.
                if (await db.TripEnquiries.AnyAsync(x => x.TripId == trip.Id && x.StepKey == stepKey && x.ListingId == listingId
                    && TripEnquiryStatuses.Open.Contains(x.Status)))
                {
                    await unitOfWork.RollbackTransactionAsync();
                    return ConflictResponse("You already asked this one.", TripEnquiryErrorTypes.AlreadyAsked);
                }

                // Open enquiries of the step use the same three places as the shortlist (a second round is refused up front).
                var openInStep = await db.TripEnquiries.CountAsync(x => x.TripId == trip.Id && x.StepKey == stepKey
                    && TripEnquiryStatuses.Open.Contains(x.Status));
                var activeInStep = await unitOfWork.TripShortlist.CountActiveAsync(trip.Id, stepKey);
                if (!TripBulkRules.StepHasRoom(openInStep, activeInStep))
                {
                    await unitOfWork.RollbackTransactionAsync();
                    return ConflictResponse(TripBulkRules.StepFullMessage, TripEnquiryErrorTypes.StepFull);
                }

                var name = TripTextSafety.Clean(listing.Name, NameMax);
                var row = await unitOfWork.TripShortlist.FindNoReplyAsync(trip.Id, stepKey, listingId);
                if (row == null)
                {
                    row = new TripShortlistItem { Id = Guid.NewGuid(), TripId = trip.Id, TravellerUserId = callerId, StepKey = stepKey, ListingId = listingId };
                    unitOfWork.TripShortlist.Add(row);
                }
                row.Kind = request.Kind;
                row.ListingName = name.Length == 0 ? request.Kind : name;
                row.JourneyId = request.JourneyId;
                row.LegIndex = (short?)request.LegIndex;
                Reopen(row, now);
                await db.SaveChangesAsync();
            }
            await unitOfWork.CommitTransactionAsync();
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // A parallel tap of the same listing won: answer with the step as it stands.
            await unitOfWork.RollbackTransactionAsync();
            db.ChangeTracker.Clear();
        }
        catch
        {
            await unitOfWork.RollbackTransactionAsync();
            throw;
        }

        var items = await unitOfWork.TripShortlist.GetActiveStepAsync(trip.Id, stepKey);
        return OkResponse(new StepShortlistDto { StepKey = stepKey, Items = items.Select(ToDto).ToList() });
    }

    private sealed record ListingFacts(Guid OwnerUserId, string Name, bool Available);

    // Null when no listing of that kind has the id. A Stay is open for guests, a Taxi needs a day rate, a Car (a journey
    // driver's taxi) only needs to be available.
    private static async Task<ListingFacts?> LoadListingAsync(ApplicationDbContext db, string kind, Guid listingId)
    {
        if (kind == TripShortlistKinds.Stay)
        {
            var stay = await db.StayListings.AsNoTracking().Where(s => s.Id == listingId)
                .Select(s => new { s.UserId, s.Name, s.Status, s.AcceptingGuests, s.IsDeleted }).FirstOrDefaultAsync();
            return stay == null ? null : new ListingFacts(stay.UserId, stay.Name ?? string.Empty,
                !stay.IsDeleted && stay.Status == StayStatuses.Approved && stay.AcceptingGuests);
        }

        var taxi = await db.TaxiListings.AsNoTracking().Where(t => t.Id == listingId)
            .Select(t => new
            {
                t.UserId, Driver = t.User.Name, Vehicle = t.VehicleType != null ? t.VehicleType.Name : null,
                t.Status, t.IsAvailable, t.IsDeleted, t.LocalDayRate,
            }).FirstOrDefaultAsync();
        if (taxi == null) return null;
        var available = !taxi.IsDeleted && taxi.Status == TaxiStatuses.Approved && taxi.IsAvailable
            && (kind == TripShortlistKinds.Car || taxi.LocalDayRate != null);
        var name = kind == TripShortlistKinds.Car ? TripBulk.CarName(taxi.Driver, taxi.Vehicle) : TripBulk.TaxiLeadName(taxi.Driver, taxi.Vehicle);
        return new ListingFacts(taxi.UserId, name, available);
    }

    // DELETE /trips/{tripId}/steps/{stepKey}/shortlist/{listingId}
    public static async Task<IResult> Remove(
        Guid tripId, string stepKey, Guid listingId, ClaimsPrincipal principal, IUnitOfWork unitOfWork, ApplicationDbContext db)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var callerId)) return UnauthorizedResponse();
        if (!TripStepKeys.IsValid(stepKey)) return BadStep();
        var (trip, error) = await LoadTripAsync(tripId, callerId, unitOfWork, db);
        if (error != null) return error;

        await unitOfWork.TripShortlist.CloseOneAsync(trip!.Id, stepKey, listingId, TripShortlistClosedReasons.Removed, DateTime.UtcNow);
        return NoContentResponse();
    }

    // POST /trips/{tripId}/steps/{stepKey}/shortlist/close
    public static async Task<IResult> Close(
        Guid tripId, string stepKey, CloseShortlistRequest request, IValidator<CloseShortlistRequest> validator,
        ClaimsPrincipal principal, IUnitOfWork unitOfWork, ApplicationDbContext db)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var callerId)) return UnauthorizedResponse();
        if (!TripStepKeys.IsValid(stepKey)) return BadStep();
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);
        var (trip, error) = await LoadTripAsync(tripId, callerId, unitOfWork, db);
        if (error != null) return error;

        if (TripGuards.TripNotCompleted(trip!) is { } completed) return completed;
        if (!TripEasyRows.StepKeysOf(TripPlanReader.Read(trip!)).Contains(stepKey)) return BadStep();

        var now = DateTime.UtcNow;
        // The open-row check and the close run under the trip lock, so a parallel send cannot slip in between.
        await unitOfWork.BeginTransactionAsync();
        try
        {
            await TripGuards.LockTripAsync(db, trip!.Id);
            // Own and Skip settle the step: not while an enquiry or a booking of it is open.
            if (await db.TripEnquiries.AnyAsync(x => x.TripId == trip.Id && x.StepKey == stepKey && TripEnquiryStatuses.Open.Contains(x.Status)))
            {
                await unitOfWork.RollbackTransactionAsync();
                return ConflictResponse("This step has open enquiries. Withdraw them first.", TripEnquiryErrorTypes.StepHasOpenEnquiries);
            }
            await unitOfWork.TripShortlist.CloseStepAsync(trip.Id, stepKey, request.Reason, now);
            // The step is settled by the traveller: no "final" push for its expired enquiries either.
            await TripBulk.StampFinalAsync(db, trip.Id, stepKey, now);
            await unitOfWork.CommitTransactionAsync();
        }
        catch
        {
            await unitOfWork.RollbackTransactionAsync();
            throw;
        }
        return NoContentResponse();
    }

    // POST /trips/{tripId}/steps/{stepKey}/mark-booked
    // One transaction: the step's unconfirmed enquiries close as booked elsewhere (open offer cards superseded), the shortlist
    // becomes Booked, and the host reminders stop with them. The hosts get no push. Idempotent: a second call finds nothing to close.
    public static async Task<IResult> MarkBooked(
        Guid tripId, string stepKey, MarkBookedRequest request, IValidator<MarkBookedRequest> validator,
        ClaimsPrincipal principal, IUnitOfWork unitOfWork, ApplicationDbContext db, ITripChatService chat, ILoggerFactory loggers)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var callerId)) return UnauthorizedResponse();
        if (!TripStepKeys.IsValid(stepKey)) return BadStep();
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);
        var (trip, error) = await LoadTripAsync(tripId, callerId, unitOfWork, db);
        if (error != null) return error;
        if (TripGuards.TripNotCompleted(trip!) is { } completed) return completed;
        if (!TripEasyRows.StepKeysOf(TripPlanReader.Read(trip!)).Contains(stepKey)) return BadStep();

        var now = DateTime.UtcNow;
        var result = new MarkBookedResultDto();
        var posts = new List<TripChatPost>();
        await unitOfWork.BeginTransactionAsync();
        try
        {
            var tripKey = trip!.Id;
            var open = await db.TripEnquiries
                .Where(x => x.TripId == tripKey && x.StepKey == stepKey && TripEnquiryStatuses.Withdrawable.Contains(x.Status)
                    && (request.ListingId == null || x.ListingId != request.ListingId))
                .ToListAsync();
            foreach (var e in open)
            {
                e.Status = TripEnquiryStatuses.Closed;
                e.FailReason = TripCloseReasons.BookedElsewhere;
                e.UpdatedAt = now;
                await TripEnquiryOps.CloseOpenOfferAsync(db, chat, e, TripOfferStatuses.Superseded, TripCardStatuses.Superseded, posts, now);
                await CloseChatAsync(db, chat, e, callerId, posts);
            }
            await db.SaveChangesAsync();
            result.ClosedEnquiries = open.Count;
            result.ClosedShortlist = await unitOfWork.TripShortlist.MarkBookedAsync(tripKey, stepKey, request.ListingId, now);
            await TripBulk.StampFinalAsync(db, tripKey, stepKey, now);
            await unitOfWork.CommitTransactionAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            await unitOfWork.RollbackTransactionAsync();
            return ConflictResponse("This enquiry just changed.", TripEnquiryErrorTypes.ConcurrentUpdate);
        }
        catch
        {
            await unitOfWork.RollbackTransactionAsync();
            throw;
        }

        try { await chat.AfterCommitAsync(posts); }
        catch (Exception ex) { loggers.CreateLogger("TripShortlist").LogError(ex, "trip.chat.broadcast_failed"); }
        return OkResponse(result);
    }

    // The host's chat after "booked elsewhere": the request card is closed (superseded) and a neutral line says why. No push.
    private static async Task CloseChatAsync(ApplicationDbContext db, ITripChatService chat, TripEnquiry e, Guid callerId, List<TripChatPost> posts)
    {
        if (e.ConversationId is not { } conversationId) return;
        if (await chat.FindCardMessageIdAsync(conversationId, TripMessageTypes.Enquiry, e.Id) is { } cardId
            && await chat.RewriteStatusAsync(cardId, TripCardStatuses.Superseded) is { } card)
            posts.Add(card);
        await TripEnquiryOps.PostSystemAsync(db, chat, e, callerId, TripSystemLines.GuestBookedElsewhere, posts);
    }
}

// Shared parts of the bulk enquiry paths (stay, local taxi, journey cars): batch lookups, idempotency, per-recipient results,
// and the in-transaction inbox row plus outbox row of a host push.
public static class TripBulk
{
    public const string IdempotencyHeader = "Idempotency-Key";
    public const string ReplayHeader = "Idempotent-Replay";

    // The body key wins; the Idempotency-Key header is the same key.
    public static Guid? RequestId(Guid? body, HttpContext http)
    {
        if (body is { } id && id != Guid.Empty) return id;
        return Guid.TryParse(http.Request.Headers[IdempotencyHeader].ToString(), out var header) && header != Guid.Empty ? header : null;
    }

    // Owners (of the given ids) the caller blocked or is blocked by, one query.
    public static async Task<HashSet<Guid>> BlockedOwnersAsync(ApplicationDbContext db, Guid callerId, IReadOnlyCollection<Guid> ownerIds)
    {
        if (ownerIds.Count == 0) return [];
        var rows = await db.UserBlocks.AsNoTracking()
            .Where(b => (b.BlockerId == callerId && ownerIds.Contains(b.BlockedId)) || (b.BlockedId == callerId && ownerIds.Contains(b.BlockerId)))
            .Select(b => b.BlockerId == callerId ? b.BlockedId : b.BlockerId)
            .ToListAsync();
        return rows.ToHashSet();
    }

    // Every requested id appears exactly once, in request order. created = enquiry ids inserted by this request,
    // updated = ids of open rows this request widened (the host was told again).
    public static List<TripSendResultDto> BuildResults(
        IReadOnlyList<Guid> requested, IReadOnlyCollection<Guid> created, IReadOnlyCollection<Guid> updated,
        IEnumerable<(Guid ListingId, string Reason)> skipped, IEnumerable<TripEnquiry> items, Guid? clientRequestId)
    {
        var skipByListing = new Dictionary<Guid, string>();
        foreach (var (listingId, reason) in skipped) skipByListing.TryAdd(listingId, reason);
        var itemByListing = new Dictionary<Guid, TripEnquiry>();
        foreach (var item in items)
        {
            if (!itemByListing.TryGetValue(item.ListingId, out var known) || created.Contains(item.Id) && !created.Contains(known.Id))
                itemByListing[item.ListingId] = item;
        }

        var results = new List<TripSendResultDto>(requested.Count);
        foreach (var id in requested)
        {
            var result = new TripSendResultDto { ListingId = id };
            if (itemByListing.TryGetValue(id, out var row))
            {
                result.EnquiryId = row.Id;
                result.Status = created.Contains(row.Id) || updated.Contains(row.Id) ? TripSendStatuses.Sent
                    : clientRequestId != null && row.ClientRequestId == clientRequestId ? TripSendStatuses.Replayed
                    : TripSendStatuses.AlreadyOpen;
            }
            else if (skipByListing.TryGetValue(id, out var reason))
            {
                result.Status = TripSendStatuses.FromSkip(reason);
                result.Reason = reason;
            }
            else
            {
                result.Status = TripSendStatuses.Failed;
                result.Reason = "Try again";
            }
            results.Add(result);
        }
        return results;
    }

    // Inbox row and push outbox row in the caller's transaction (written by the caller's SaveChanges); live collects the
    // rows for the hub event sent after the commit.
    // key null = the new-row key enq:{id}:new; a push about a row that already exists passes a unique key (TripBulkRules.UpdateKey).
    public static void StagePush(
        ApplicationDbContext db, IOutboxWriter outbox, NotificationEvent n, Guid enquiryId, DateTime now, ICollection<NotificationEvent> live,
        string? key = null)
    {
        if (n.TargetUserId is not { } userId) return;
        db.NotificationEvents.Add(n);
        live.Add(n);
        outbox.AddPush(new OutboxPushPayload(
            key ?? $"enq:{enquiryId:N}:new", userId, n.Id, n.Title, n.Body, n.ActionRoute, n.ActionArgumentsJson, $"trip-enq-{enquiryId:N}"), now);
    }

    // The name a listing carries on the shortlist and on its enquiry: the driver alone for a local taxi, "Driver - Vehicle" for a journey car.
    public static string TaxiLeadName(string? driver, string? vehicle)
    {
        var name = TripTextSafety.Clean(driver, 120);
        if (name.Length > 0) return name;
        var type = TripTextSafety.Clean(vehicle, 120);
        return type.Length == 0 ? "Taxi" : type;
    }

    public static string CarName(string? driver, string? vehicle)
    {
        var d = TripTextSafety.Clean(driver, 120);
        var v = TripTextSafety.Clean(vehicle, 120);
        var name = d.Length > 0 && v.Length > 0 ? $"{d} \u00b7 {v}" : d.Length > 0 ? d : v;
        if (name.Length == 0) return "Taxi";
        return name.Length <= 120 ? name : name[..120].TrimEnd();
    }

    // After a send: Active rows whose listing could not be asked (deleted, off, blocked, own) close as Unavailable, so the card
    // never offers them again and no reminder counts them. Runs in every send path, also when nothing was sent. Never fails the send.
    public static async Task CloseUnavailableAsync(
        ApplicationDbContext db, Guid tripId, string? stepKey, IEnumerable<(Guid ListingId, string Reason)> skipped, DateTime now, ILogger logger)
    {
        // A send without a step has no shortlist rows; the rule is per step (a host off for one city may be right for another step).
        if (stepKey == null) return;
        var ids = skipped.Where(x => TripBulkRules.ClosesShortlistRow(x.Reason)).Select(x => x.ListingId).Distinct().ToList();
        if (ids.Count == 0) return;
        try
        {
            await db.TripShortlistItems
                .Where(x => x.TripId == tripId && x.StepKey == stepKey && ids.Contains(x.ListingId)
                    && x.State == TripShortlistStates.Active)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.State, TripShortlistStates.Closed)
                    .SetProperty(x => x.ClosedReason, TripShortlistClosedReasons.Unavailable)
                    .SetProperty(x => x.ClosedAt, now));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "trip.shortlist.close_unavailable_failed {TripId}", tripId);
        }
    }

    // The enquiry made from a Sent shortlist row ended without a booking (declined, answered not available, cancelled): the row closes.
    public static async Task CloseSentRowAsync(ApplicationDbContext db, TripEnquiry e, DateTime now)
    {
        if (e.TripId is not { } tripId || e.StepKey is not { } stepKey) return;
        await db.TripShortlistItems
            .Where(x => x.TripId == tripId && x.StepKey == stepKey && x.ListingId == e.ListingId && x.State == TripShortlistStates.Sent)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.State, TripShortlistStates.Closed)
                .SetProperty(x => x.ClosedReason, TripShortlistClosedReasons.Declined)
                .SetProperty(x => x.ClosedAt, now));
    }

    // The traveller settled the step (booked elsewhere or skipped): an Expired enquiry of it never gets the final push.
    public static async Task StampFinalAsync(ApplicationDbContext db, Guid tripId, string stepKey, DateTime now)
        => await db.TripEnquiries
            .Where(x => x.TripId == tripId && x.StepKey == stepKey && x.Status == TripEnquiryStatuses.Expired && x.FinalPushedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.FinalPushedAt, now));

    // Inserts the rows with ONE SaveChanges. When a parallel send took one of the open slots (race(ex)), the batch is
    // retried row by row and the rows that lost are returned (detached).
    public static async Task<List<TripEnquiry>> SaveRowsAsync(ApplicationDbContext db, List<TripEnquiry> rows, Func<DbUpdateException, bool> race)
    {
        var lost = new List<TripEnquiry>();
        if (rows.Count == 0) return lost;
        db.TripEnquiries.AddRange(rows);
        try
        {
            await db.SaveChangesAsync();
            return lost;
        }
        catch (DbUpdateException ex) when (race(ex))
        {
            foreach (var row in rows) db.Entry(row).State = EntityState.Detached;
        }
        foreach (var row in rows)
        {
            db.TripEnquiries.Add(row);
            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException ex) when (race(ex))
            {
                db.Entry(row).State = EntityState.Detached;
                lost.Add(row);
            }
        }
        return lost;
    }
}
