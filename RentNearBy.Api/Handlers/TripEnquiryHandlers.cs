using System.Security.Claims;
using System.Text.Json;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Npgsql;
using RentNearBy.Api.Filters;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

// Side checks for every enquiry endpoint: the caller must be the traveller or the owner the action is for.
public static class TripEnquiryAuth
{
    public const string Traveller = "traveller";
    public const string Owner = "owner";

    public static IResult NotFound() => ErrorResponse(404, "Enquiry not found", TripEnquiryErrorTypes.TripEnquiryNotFound);

    public static IResult? RequireTraveller(TripEnquiry? e, Guid callerId)
    {
        if (e == null) return NotFound();
        if (e.TravellerUserId != callerId)
            return ForbiddenResponse("Only the guest can do this.", TripEnquiryErrorTypes.NotEnquiryTraveller);
        return null;
    }

    // The caller must be the enquiry's owner AND still own the live listing.
    public static async Task<IResult?> RequireOwnerAsync(TripEnquiry? e, Guid callerId, ApplicationDbContext db)
    {
        if (e == null) return NotFound();
        var forbidden = ForbiddenResponse(
            e.Kind == TripEnquiryKinds.Taxi ? "Only the driver can do this." : "Only the host can do this.",
            TripEnquiryErrorTypes.NotEnquiryOwner);
        if (e.OwnerUserId != callerId) return forbidden;
        var owns = e.Kind == TripEnquiryKinds.Taxi
            ? await db.TaxiListings.AsNoTracking().AnyAsync(t => t.Id == e.ListingId && t.UserId == callerId && !t.IsDeleted)
            : await db.StayListings.AsNoTracking().AnyAsync(s => s.Id == e.ListingId && s.UserId == callerId && !s.IsDeleted);
        return owns ? null : forbidden;
    }

    public static IResult? RequireParty(TripEnquiry? e, Guid callerId, out string side)
    {
        side = string.Empty;
        if (e == null) return NotFound();
        if (e.TravellerUserId == callerId) { side = Traveller; return null; }
        if (e.OwnerUserId == callerId) { side = Owner; return null; }
        return ForbiddenResponse("Forbidden");
    }
}

public static class TripEnquiryHandlers
{
    internal const string OpenUniqueIndex = "ix_tripenquiries_open_unique";
    private const string OpenTaxiUniqueIndex = "ix_tripenquiries_open_taxi_unique";
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);
    private static readonly TimeSpan Day24 = TimeSpan.FromHours(24);

    private static IResult Concurrent()
        => ConflictResponse("This enquiry just changed.", TripEnquiryErrorTypes.ConcurrentUpdate);

    private static IResult Closed(string message)
        => ConflictResponse(message, TripEnquiryErrorTypes.TripEnquiryClosed);

    private static IResult TripNotFound() => ErrorResponse(404, "Trip not found", TripErrorTypes.TripNotFound);

    private static IResult Invalid(FluentValidation.Results.ValidationResult v) => BadRequestResponse(v.Errors[0].ErrorMessage);

    // ---------- POST /trips/{tripId}/enquiries ----------

    public static async Task<IResult> Create(
        Guid tripId, CreateTripEnquiryRequest request, IValidator<CreateTripEnquiryRequest> validator, ClaimsPrincipal principal,
        IUnitOfWork unitOfWork, ApplicationDbContext db, IConversationFactory conversations, ITripChatService chat,
        ITripNotifier notifier, IRateLimitService rateLimiter, IMemoryCache cache, ILoggerFactory loggers, HttpContext http,
        IOutboxWriter outbox)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var callerId)) return UnauthorizedResponse();

        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
            return validation.Errors[0].ErrorMessage == TripExecutionRules.PickMessage
                ? BadRequestResponse(TripExecutionRules.PickMessage, TripEnquiryErrorTypes.TooManyRecipients)
                : Invalid(validation);

        var trip = await unitOfWork.Trips.GetByIdForOwnerAsync(tripId, callerId);
        if (trip == null) return TripNotFound();

        request.ClientRequestId = TripBulk.RequestId(request.ClientRequestId, http);
        return await CreateCoreAsync(trip, request, callerId, [], unitOfWork, db, conversations, chat, notifier, rateLimiter, cache, loggers, http, outbox);
    }

    // The whole send path of Create after the trip is loaded; Ask again reuses it. preSkipped rows lead the Skipped list.
    private static async Task<IResult> CreateCoreAsync(
        Trip trip, CreateTripEnquiryRequest request, Guid callerId, IReadOnlyList<TripSkippedDto> preSkipped,
        IUnitOfWork unitOfWork, ApplicationDbContext db, IConversationFactory conversations, ITripChatService chat,
        ITripNotifier notifier, IRateLimitService rateLimiter, IMemoryCache cache, ILoggerFactory loggers, HttpContext http,
        IOutboxWriter outbox)
    {
        if (trip.Status == TripStatuses.Completed)
            return ConflictResponse("This trip is complete. Plan changes are off.", TripEnquiryErrorTypes.TripCompleted);

        if (request.Kind == TripEnquiryKinds.Taxi)
            return await CreateLeadsAsync(trip, request, callerId, preSkipped, unitOfWork, db, notifier, chat, rateLimiter, cache, loggers, http, outbox);

        var now = DateTime.UtcNow;
        if (CheckNight(trip, request.Day, now) is { } nightError) return nightError;
        var plan = ReadPlan(trip);
        if (request.StepKey is { } sendStep
            && TripGuards.StepMismatch(plan, sendStep, TripShortlistKinds.Stay, request.Day) is { } mismatch) return TripGuards.StepMismatchError(mismatch);
        // No stepKey: the step is derived from the nights, so a settled step stays closed to a forged send.
        if (TripGuards.StepClosed(plan, request.StepKey ?? TripGuards.DerivedStayStep(plan, request.Day))) return TripGuards.StepClosedError();
        var (checkIn, checkOut) = Nights(trip, plan, request.Day, request.AllNights, request.Nights);
        var multiNight = request.Nights != null ? TripExecutionRules.Nights(checkIn, checkOut) > 1 : request.AllNights;

        var tabs = await ConfigHandlers.GetAppTabsCachedAsync(unitOfWork, cache);
        if (!TabState.IsKindActive(tabs, ListingKinds.Stay))
            return ForbiddenResponse("Stays are not available right now.", TabGateFilter.ErrorType);

        var travellerName = TripTextSafety.Clean(
            await db.Users.AsNoTracking().Where(u => u.Id == callerId).Select(u => u.Name).FirstOrDefaultAsync());

        var tripOpen = (await unitOfWork.TripEnquiries.GetOpenByTripAsync(trip.Id, TripEnquiryKinds.Stay)).ToList();
        // The area of the leg these nights are in: a host moved to another district or state since the shortlist is not asked.
        var legDistrict = LegDistrict(trip, plan, request.Day);
        var items = new List<TripEnquiry>();
        var skipped = new List<TripSkippedDto>(preSkipped);
        var candidates = new List<StaySnapshot>();
        var stayMerges = new List<TripEnquiry>();
        var created = new HashSet<Guid>();
        var live = new List<NotificationEvent>();
        var day = (short)request.Day;

        // Duplicates dropped; a retry of the same clientRequestId never sends again (its rows come back as Replayed).
        var requested = request.ListingIds.Distinct().ToList();
        var toProcess = requested;
        var staleIds = new HashSet<Guid>();
        if (request.ClientRequestId is { } requestId)
        {
            // Only an open row of this trip, step and kind is a replay; any other row holding the id is sent as new without the id.
            var (replay, stale) = TripGuards.SplitReplay(
                await unitOfWork.TripEnquiries.GetByClientRequestAsync(callerId, requestId, requested), trip.Id, TripEnquiryKinds.Stay, request.StepKey, now);
            staleIds = stale;
            items.AddRange(replay);
            var replayed = replay.Select(r => r.ListingId).ToHashSet();
            toProcess = requested.Where(id => !replayed.Contains(id)).ToList();
        }

        // A step send is for shortlisted hosts only (hosts already open in the step are handled below as open rows).
        if (request.StepKey is { } shortStep && toProcess.Count > 0)
        {
            var open = tripOpen.Where(x => x.StepKey == shortStep).Select(x => x.ListingId).ToHashSet();
            if ((await TripGuards.NotShortlistedAsync(db, trip.Id, callerId, shortStep, toProcess, open)).Count > 0)
                return TripGuards.NotShortlistedError();
        }

        // One query each for the stays, the blocks and the traveller's own open rows (no per-listing lookups).
        var staysById = await LoadStaysAsync(db, toProcess);
        var blockedOwners = await TripBulk.BlockedOwnersAsync(db, callerId, staysById.Values.Select(x => x.UserId).Distinct().ToList());
        var mineByListing = (await unitOfWork.TripEnquiries.GetOpenByTravellerListingsAsync(callerId, TripEnquiryKinds.Stay, toProcess))
            .ToLookup(x => x.ListingId);

        foreach (var listingId in toProcess)
        {
            staysById.TryGetValue(listingId, out var stay);
            if (stay == null || stay.IsDeleted || stay.Status != StayStatuses.Approved || !stay.AcceptingGuests || stay.UserId == callerId
                || !TripBulkRules.HostInLegArea(stay.DistrictId, legDistrict))
            {
                skipped.Add(Skip(listingId, TripSkipReasons.NotAvailable));
                continue;
            }
            if (blockedOwners.Contains(stay.UserId))
            {
                skipped.Add(Skip(listingId, TripSkipReasons.Blocked));
                continue;
            }

            // Matched by the night's date: a row whose check-in moved no longer stands for its old Day number.
            var same = tripOpen.FirstOrDefault(x => x.ListingId == listingId && CoversDay(x, day, checkIn));
            if (same != null && same.CheckIn <= checkIn && checkOut <= same.CheckOut)
            {
                items.Add(same);
                continue;
            }

            // One open unconfirmed row per stay and trip, so the shared chat always acts on the right row:
            // an Asked row next to or overlapping the new nights takes their union; any other unconfirmed row blocks a second one.
            var unconfirmed = tripOpen.FirstOrDefault(x => x.ListingId == listingId && TripEnquiryStatuses.IsUnconfirmed(x.Status));
            var joinable = unconfirmed is { Status: TripEnquiryStatuses.Asked }
                && (Adjacent(unconfirmed, checkIn, checkOut) || Overlaps(unconfirmed, checkIn, checkOut))
                && JoinStaysInOneLeg(trip, plan, unconfirmed, checkIn, checkOut);

            // Open rows of this traveller for the same host and nights: one of this trip means the host knows; another trip's row
            // means nothing was sent for this trip.
            var overlapping = mineByListing[listingId]
                .Where(x => Overlaps(x, checkIn, checkOut) && !(joinable && x.Id == unconfirmed!.Id)).ToList();
            if (overlapping.Count > 0)
            {
                skipped.Add(Skip(listingId, TripBulkRules.OverlapSkipReason(overlapping, trip.Id)));
                continue;
            }

            if (unconfirmed != null)
            {
                if (joinable && TripExecutionRules.FitsNightCap(tripOpen, listingId, candidates.Count + stayMerges.Count, checkIn, checkOut))
                    stayMerges.Add(unconfirmed);
                else
                    // The open row does not hold all the requested nights: nothing was sent for the rest.
                    skipped.Add(Skip(listingId, joinable ? TripSkipReasons.LimitReached : TripSkipReasons.OpenForOtherNights));
                continue;
            }

            if (!TripExecutionRules.FitsNightCap(tripOpen, listingId, candidates.Count + stayMerges.Count, checkIn, checkOut))
            {
                skipped.Add(Skip(listingId, TripSkipReasons.LimitReached));
                continue;
            }
            candidates.Add(stay);
        }

        // Only a request that sends or widens an enquiry spends an ask; a request that writes nothing gives it back.
        var asks = new RateLimitPermits(rateLimiter);
        if (candidates.Count > 0 || stayMerges.Count > 0)
            if (await EnquiryRateLimitedAsync(asks, http, callerId, asksHeader: true) is { } limited) return limited;

        var (expiresAt, veryShortNotice) = TripExecutionRules.EnquiryExpiry(trip.StartDate!.Value, checkIn, now);
        var posts = new List<TripChatPost>();
        var pushes = new List<TripPendingPush>();
        var updated = new List<Guid>();
        var counted = new List<Guid>();

        if (candidates.Count > 0 || stayMerges.Count > 0)
        {
            var context = BuildContext(trip, plan, request.Day, checkIn, checkOut, multiNight, travellerName);
            var contextJson = JsonSerializer.Serialize(context, TripJson.Options);
            var isUrgent = TripExecutionRules.IsUrgent(trip.StartDate!.Value, now);

            await unitOfWork.BeginTransactionAsync();
            try
            {
                // Parallel sends on one trip are serialised here; the overlap and night-cap rules are re-checked on fresh rows.
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Trips\" WHERE \"Id\" = {trip.Id} FOR UPDATE");
                var fresh = await db.TripEnquiries.AsNoTracking()
                    .Where(x => x.TripId == trip.Id && x.Kind == TripEnquiryKinds.Stay && TripEnquiryStatuses.Open.Contains(x.Status))
                    .ToListAsync();
                var (mergesNow, candidatesNow) = RecheckUnderLock(fresh, stayMerges, candidates, checkIn, checkOut, items, skipped);
                if (mergesNow == null)
                {
                    await unitOfWork.RollbackTransactionAsync();
                    await asks.ReleaseAllAsync();
                    return Concurrent();
                }
                if (request.StepKey is { } capStep && candidatesNow.Count > 0)
                {
                    var openInStep = await db.TripEnquiries.CountAsync(x => x.TripId == trip.Id && x.StepKey == capStep
                        && TripEnquiryStatuses.Open.Contains(x.Status));
                    if (!TripGuards.StepHasRoomFor(openInStep, candidatesNow.Count))
                    {
                        await unitOfWork.RollbackTransactionAsync();
                        await asks.ReleaseAllAsync();
                        return TripGuards.StepFullError();
                    }
                }

                // Next to an Asked row of the same stay: the row takes the joined nights and the host is told again.
                foreach (var row in mergesNow)
                {
                    var start = trip.StartDate!.Value;
                    var mergedIn = row.CheckIn!.Value < checkIn ? row.CheckIn.Value : checkIn;
                    var mergedOut = row.CheckOut!.Value > checkOut ? row.CheckOut.Value : checkOut;
                    var mergedDay = mergedIn.DayNumber - start.DayNumber + 1;
                    var allNights = mergedOut == Nights(trip, plan, mergedDay, allNights: true).CheckOut;
                    var mergedContext = BuildContext(trip, plan, mergedDay, mergedIn, mergedOut, allNights, travellerName);
                    row.Day = (short)mergedDay;
                    row.CheckIn = mergedIn;
                    row.CheckOut = mergedOut;
                    row.ContextJson = JsonSerializer.Serialize(mergedContext, TripJson.Options);
                    row.ExpiresAt = TripExecutionRules.EnquiryExpiry(start, mergedIn, now).ExpiresAt;
                    row.IsUrgent = isUrgent;
                    row.UpdatedAt = now;
                    await db.SaveChangesAsync();

                    var rowConversation = row.ConversationId is { } rowConversationId
                        ? await db.Conversations.FirstOrDefaultAsync(c => c.Id == rowConversationId) : null;
                    if (rowConversation != null)
                        posts.Add(await chat.PostAsync(rowConversation, callerId, TripMessageTypes.Enquiry,
                            EnquiryCardPayload(row, mergedContext), mergedContext.Text));

                    items.Add(row);
                    updated.Add(row.Id);
                    // The "updated nights" push is an outbox row of this transaction (durable), one unique key per change.
                    var mergeArgs = rowConversation != null && staysById.TryGetValue(row.ListingId, out var mergeStay)
                        ? StayChatArgs(row, mergeStay, rowConversation, travellerName) : null;
                    TripBulk.StagePush(db, outbox,
                        EnquiryNightsUpdated(row, new TripPushContext(row.OwnerUserId ?? Guid.Empty, travellerName, row.ListingName, mergeArgs)),
                        row.Id, now, live, TripBulkRules.UpdateKey(row.Id, "upd", now));
                }

                var newRows = new List<(TripEnquiry Row, StaySnapshot Stay, Conversation Conversation)>();
                foreach (var stay in candidatesNow)
                {
                    var conv = await conversations.GetOrCreateAsync(callerId, ListingKinds.Stay, stay.Id, skipRateLimit: true, skipNewAccountCooldown: true);
                    if (conv.Conversation == null || conv.Status is not (200 or 201))
                    {
                        if (conv.Status is 400 or 404 or 409)
                        {
                            skipped.Add(Skip(stay.Id, TripSkipReasons.NotAvailable));
                            continue;
                        }
                        await unitOfWork.RollbackTransactionAsync();
                        await asks.ReleaseAllAsync();
                        return ErrorResponse(conv.Status, conv.Message ?? "Could not start the chat.", conv.ErrorType ?? "BadRequest");
                    }

                    newRows.Add((new TripEnquiry
                    {
                        Id = Guid.NewGuid(),
                        TripId = trip.Id,
                        TravellerUserId = callerId,
                        OwnerUserId = stay.UserId,
                        Kind = TripEnquiryKinds.Stay,
                        ListingId = stay.Id,
                        ListingName = ListingName(stay),
                        ListingLatitude = stay.Latitude,
                        ListingLongitude = stay.Longitude,
                        Day = day,
                        CheckIn = checkIn,
                        CheckOut = checkOut,
                        ConversationId = conv.Conversation.Id,
                        ContextJson = contextJson,
                        Status = TripEnquiryStatuses.Asked,
                        ExpiresAt = expiresAt,
                        IsUrgent = isUrgent,
                        AdvanceStatus = TripAdvanceStatuses.None,
                        StepKey = request.StepKey,
                        ClientRequestId = staleIds.Contains(stay.Id) ? null : request.ClientRequestId,
                        CreatedAt = now,
                        UpdatedAt = now,
                    }, stay, conv.Conversation));
                }

                // One SaveChanges for every new row; a parallel send that took a night answers with that row.
                var lost = await TripBulk.SaveRowsAsync(db, newRows.Select(x => x.Row).ToList(), IsOpenUniqueViolation);
                foreach (var (e, stay, conversation) in newRows)
                {
                    if (lost.Contains(e))
                    {
                        var winner = await unitOfWork.TripEnquiries.FindOpenAsync(trip.Id, TripEnquiryKinds.Stay, stay.Id, day);
                        if (winner != null && TripGuards.CoversNights(winner, checkIn, checkOut)) items.Add(winner);
                        else skipped.Add(Skip(stay.Id, winner != null ? TripSkipReasons.OpenForOtherNights : TripSkipReasons.AlreadyOpen));
                        continue;
                    }

                    posts.Add(await chat.PostAsync(conversation, callerId, TripMessageTypes.Enquiry,
                        EnquiryCardPayload(e, context), context.Text));
                    created.Add(e.Id);
                    items.Add(e);
                    var pushContext = new TripPushContext(stay.UserId, travellerName, e.ListingName, StayChatArgs(e, stay, conversation, travellerName));
                    TripBulk.StagePush(db, outbox, TripNotifications.TripEnquiryReceived(e, pushContext), e.Id, now, live);
                }

                var sentIds = newRows.Where(x => created.Contains(x.Row.Id)).Select(x => x.Stay.Id).ToList();
                if (sentIds.Count > 0)
                {
                    counted.AddRange(sentIds);
                    await unitOfWork.TripShortlist.MarkSentAsync(trip.Id, request.StepKey, sentIds, now);
                }
                await db.SaveChangesAsync();
                await PromoteToPlannedAsync(db, trip.Id);
                await unitOfWork.CommitTransactionAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                // The host answered the enquiry while its nights were being joined.
                await unitOfWork.RollbackTransactionAsync();
                await asks.ReleaseAllAsync();
                return Concurrent();
            }
            catch
            {
                await unitOfWork.RollbackTransactionAsync();
                await asks.ReleaseAllAsync();
                throw;
            }
        }

        // Host counters are bumped after commit: inside the transaction they would hold the host row lock while parallel travellers queue on a popular stay.
        if (counted.Count > 0)
            await db.StayListings.Where(x => counted.Contains(x.Id))
                .ExecuteUpdateAsync(x => x.SetProperty(y => y.TripEnquiriesReceived, y => y.TripEnquiriesReceived + 1));

        // Hosts that could not be asked leave the shortlist, whether or not anything was sent.
        await TripBulk.CloseUnavailableAsync(db, trip.Id, request.StepKey, skipped.Select(x => (x.ListingId, x.Reason)), now, loggers.CreateLogger("TripBulk"));

        if (updated.Count == 0 && created.Count == 0) await asks.ReleaseAllAsync();
        outbox.Wake();
        await notifier.PublishLiveAsync(live);
        await AfterCommitAsync(chat, posts, pushes, unitOfWork, notifier, loggers);

        return BatchResponse(trip, new TripEnquiryBatchDto
        {
            Items = await TripEnquiryDtoBuilder.BuildManyAsync(unitOfWork, items, callerId, now),
            Skipped = skipped,
            Updated = updated,
            VeryShortNotice = veryShortNotice,
            Results = TripBulk.BuildResults(requested, created, updated, skipped.Select(x => (x.ListingId, x.Reason)), items, request.ClientRequestId),
        }, http, replayOnly: created.Count == 0 && updated.Count == 0 && request.ClientRequestId != null && items.Count > 0 && toProcess.Count == 0);
    }

    // 201 for a send; 200 + Idempotent-Replay when the whole request was already sent under this clientRequestId.
    private static IResult BatchResponse(Trip trip, TripEnquiryBatchDto dto, HttpContext http, bool replayOnly)
    {
        if (!replayOnly) return CreatedResponse(dto, $"/api/v1/trips/{trip.Id}");
        http.Response.Headers[TripBulk.ReplayHeader] = "true";
        return OkResponse(dto);
    }

    // Chat target of the host push, built from rows already in hand (the repository builder costs five queries per push).
    private static string StayChatArgs(TripEnquiry e, StaySnapshot stay, Conversation conversation, string travellerName)
        => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["conversationId"] = conversation.Id,
            ["listingType"] = conversation.ListingType,
            ["listingId"] = conversation.ListingId,
            ["stayTypeId"] = stay.StayTypeId,
            ["otherPartyId"] = e.TravellerUserId,
            ["otherPartyName"] = travellerName.Length == 0 ? "User" : travellerName,
            ["listingTitle"] = stay.Name ?? e.ListingName,
            ["area"] = stay.CityName ?? stay.DistrictName,
            ["isOwner"] = true,
            ["enquiryId"] = e.Id,
        });

    // Under the trip row lock: a joined row must still be the same Asked range (else null = concurrent change); a new
    // stay is answered with a fresh row that already holds the nights, skipped when the stay has another open row on the
    // trip, and every row must still fit the night cap.
    private static (List<TripEnquiry>? Merges, List<StaySnapshot> Candidates) RecheckUnderLock(
        List<TripEnquiry> fresh, List<TripEnquiry> stayMerges, List<StaySnapshot> candidates, DateOnly checkIn, DateOnly checkOut,
        List<TripEnquiry> items, List<TripSkippedDto> skipped)
    {
        var merges = new List<TripEnquiry>();
        var news = new List<StaySnapshot>();
        var added = 0;
        foreach (var row in stayMerges)
        {
            var now = fresh.FirstOrDefault(x => x.Id == row.Id);
            if (now == null || now.Status != TripEnquiryStatuses.Asked || now.CheckIn != row.CheckIn || now.CheckOut != row.CheckOut)
                return (null, news);
            if (!TripExecutionRules.FitsNightCap(fresh, row.ListingId, added, checkIn, checkOut))
            {
                skipped.Add(Skip(row.ListingId, TripSkipReasons.LimitReached));
                continue;
            }
            merges.Add(row);
            added++;
        }

        foreach (var stay in candidates)
        {
            var mine = fresh.Where(x => x.ListingId == stay.Id).ToList();
            if (mine.FirstOrDefault(x => x.CheckIn <= checkIn && checkOut <= x.CheckOut) is { } holder)
            {
                items.Add(holder);
                continue;
            }
            if (mine.Any(x => TripEnquiryStatuses.IsUnconfirmed(x.Status) || Overlaps(x, checkIn, checkOut)))
            {
                skipped.Add(Skip(stay.Id, TripSkipReasons.OpenForOtherNights));
                continue;
            }
            if (!TripExecutionRules.FitsNightCap(fresh, stay.Id, added, checkIn, checkOut))
            {
                skipped.Add(Skip(stay.Id, TripSkipReasons.LimitReached));
                continue;
            }
            news.Add(stay);
            added++;
        }
        return (merges, news);
    }

    private static object EnquiryCardPayload(TripEnquiry e, TripEnquiryContext context) => new
    {
        enquiryId = e.Id,
        checkIn = IsoDate(e.CheckIn),
        checkOut = IsoDate(e.CheckOut),
        nights = TripExecutionRules.Nights(e.CheckIn, e.CheckOut),
        adults = context.Adults,
        kids = context.Kids,
        rooms = context.Rooms,
        text = context.Text,
    };

    private static NotificationEvent EnquiryNightsUpdated(TripEnquiry e, TripPushContext ctx)
    {
        var n = TripNotifications.TripEnquiryReceived(e, ctx);
        n.Title += " (updated nights)";
        return n;
    }

    // The new nights touch the row's range (night before check-in or the check-out night) without overlapping it.
    private static bool Adjacent(TripEnquiry x, DateOnly checkIn, DateOnly checkOut)
        => x.CheckIn is { } a && x.CheckOut is { } b && (checkOut == a || checkIn == b);

    // ---------- POST /trips/{tripId}/enquiries, kind Taxi (one-way lead, no conversation) ----------

    private static async Task<IResult> CreateLeadsAsync(
        Trip trip, CreateTripEnquiryRequest request, Guid callerId, IReadOnlyList<TripSkippedDto> preSkipped,
        IUnitOfWork unitOfWork, ApplicationDbContext db,
        ITripNotifier notifier, ITripChatService chat, IRateLimitService rateLimiter, IMemoryCache cache, ILoggerFactory loggers,
        HttpContext http, IOutboxWriter outbox)
    {
        var now = DateTime.UtcNow;
        var days = (request.Days ?? []).Distinct().OrderBy(d => d).ToList();
        if (CheckTaxiDays(trip, days, now) is { } dayError) return dayError;
        var plan = ReadPlan(trip);
        if (CheckDaysInOneLeg(trip, plan, days) is { } legError) return legError;
        var leg = (short)plan.LegIndexOf(days[0]);
        if (request.StepKey is { } sendStep
            && TripGuards.StepMismatch(plan, sendStep, TripShortlistKinds.Taxi) is { } mismatch) return TripGuards.StepMismatchError(mismatch);
        if (TripGuards.StepClosed(plan, request.StepKey ?? TripRowKinds.Taxi)) return TripGuards.StepClosedError();
        var (checkIn, checkOut) = TaxiSpan(trip, days);

        var tabs = await ConfigHandlers.GetAppTabsCachedAsync(unitOfWork, cache);
        if (!TabState.IsKindActive(tabs, ListingKinds.Taxi))
            return ForbiddenResponse("Taxis are not available right now.", TabGateFilter.ErrorType);

        var travellerName = TripTextSafety.Clean(
            await db.Users.AsNoTracking().Where(u => u.Id == callerId).Select(u => u.Name).FirstOrDefaultAsync());

        // Journey cars between cities run beside local taxis: they never merge, block or count against the lead cap.
        var tripOpen = (await unitOfWork.TripEnquiries.GetOpenByTripAsync(trip.Id, TripEnquiryKinds.Taxi))
            .Where(x => !TripSegmentRows.IsSegment(x)).ToList();
        // A taxi based in another district or state than this leg is not on this leg.
        var legDistrict = LegDistrict(trip, plan, days[0]);
        var items = new List<TripEnquiry>();
        var skipped = new List<TripSkippedDto>(preSkipped);
        var candidates = new List<TaxiSnapshot>();
        var merges = new List<(TripEnquiry Row, List<int> Days)>();
        var created = new HashSet<Guid>();
        var live = new List<NotificationEvent>();

        // Duplicates dropped; a retry of the same clientRequestId never sends again (its rows come back as Replayed).
        var requested = request.ListingIds.Distinct().ToList();
        var toProcess = requested;
        var staleIds = new HashSet<Guid>();
        if (request.ClientRequestId is { } requestId)
        {
            var (replay, stale) = TripGuards.SplitReplay(
                await unitOfWork.TripEnquiries.GetByClientRequestAsync(callerId, requestId, requested), trip.Id, TripEnquiryKinds.Taxi, request.StepKey, now);
            staleIds = stale;
            items.AddRange(replay);
            var replayed = replay.Select(r => r.ListingId).ToHashSet();
            toProcess = requested.Where(id => !replayed.Contains(id)).ToList();
        }

        if (request.StepKey is { } shortStep && toProcess.Count > 0)
        {
            var open = tripOpen.Where(x => x.StepKey == shortStep).Select(x => x.ListingId).ToHashSet();
            if ((await TripGuards.NotShortlistedAsync(db, trip.Id, callerId, shortStep, toProcess, open)).Count > 0)
                return TripGuards.NotShortlistedError();
        }

        // One query each for the taxis, the blocks and the traveller's own open rows (no per-listing lookups).
        var taxisById = await LoadTaxisAsync(db, toProcess);
        var blockedOwners = await TripBulk.BlockedOwnersAsync(db, callerId, taxisById.Values.Select(x => x.UserId).Distinct().ToList());
        var mineByListing = (await unitOfWork.TripEnquiries.GetOpenByTravellerListingsAsync(callerId, TripEnquiryKinds.Taxi, toProcess))
            .ToLookup(x => x.ListingId);

        foreach (var listingId in toProcess)
        {
            taxisById.TryGetValue(listingId, out var taxi);
            if (taxi == null || taxi.IsDeleted || taxi.Status != TaxiStatuses.Approved || !taxi.IsAvailable
                || taxi.LocalDayRate == null || taxi.UserId == callerId || !TripBulkRules.HostInLegArea(taxi.DistrictId, legDistrict))
            {
                skipped.Add(Skip(listingId, TripSkipReasons.NotAvailable));
                continue;
            }
            if (blockedOwners.Contains(taxi.UserId))
            {
                skipped.Add(Skip(listingId, TripSkipReasons.Blocked));
                continue;
            }

            // Journey leads (no trip) are a separate kind: the same driver can be the journey car and the local taxi.
            var mine = mineByListing[listingId].Where(x => x.TripId != null && !TripSegmentRows.IsSegment(x)).ToList();

            var same = tripOpen.FirstOrDefault(x => x.ListingId == listingId && x.Day == null && x.CityLeg == leg);
            if (same != null)
            {
                // Held days by date (D7): a start move keeps the row's dates, so they are renumbered from the current start.
                var held = CurrentTaxiDays(same, trip.StartDate!.Value);
                var merged = TripLeadRules.MergeDays(held, days);
                if (merged == null)
                {
                    // Nothing new to ask: the driver already has these days.
                    skipped.Add(Skip(listingId, TripSkipReasons.AlreadyOpen));
                    continue;
                }
                if (!TripLeadRules.CanMergeDays(same.Status))
                {
                    skipped.Add(Skip(listingId, TripSkipReasons.AlreadyBooked));
                    continue;
                }
                var (mergedIn, mergedOut) = TaxiSpan(trip, merged);
                var mergeOverlap = mine.Where(x => x.Id != same.Id && Overlaps(x, mergedIn, mergedOut)).ToList();
                if (mergeOverlap.Count > 0)
                {
                    skipped.Add(Skip(listingId, TripBulkRules.OverlapSkipReason(mergeOverlap, trip.Id)));
                    continue;
                }
                if (!FitsLeadCap(tripOpen, listingId, candidates.Count + merges.Count, DatesOf(trip.StartDate!.Value, merged.Except(held))))
                {
                    skipped.Add(Skip(listingId, TripSkipReasons.LimitReached));
                    continue;
                }
                merges.Add((same, merged));
                continue;
            }

            var overlapping = mine.Where(x => Overlaps(x, checkIn, checkOut)).ToList();
            if (overlapping.Count > 0)
            {
                skipped.Add(Skip(listingId, TripBulkRules.OverlapSkipReason(overlapping, trip.Id)));
                continue;
            }

            if (!FitsLeadCap(tripOpen, listingId, candidates.Count + merges.Count, DatesOf(trip.StartDate!.Value, days)))
            {
                skipped.Add(Skip(listingId, TripSkipReasons.LimitReached));
                continue;
            }
            candidates.Add(taxi);
        }

        // Only a request that sends or widens a lead spends the ask and lead quotas; a request that writes nothing gives them back.
        var quotas = new RateLimitPermits(rateLimiter);
        if (candidates.Count > 0 || merges.Count > 0)
        {
            if (await EnquiryRateLimitedAsync(quotas, http, callerId, asksHeader: true) is { } limited) return limited;
            var leadLimit = await quotas.TakeAsync($"trip:lead:{trip.Id}", TripLeadRules.LeadsPerTripPerDay, Day24);
            if (!leadLimit.IsAllowed)
            {
                http.Response.Headers["Retry-After"] = ((int)Math.Ceiling((leadLimit.RetryAfter ?? Day24).TotalSeconds)).ToString();
                return ErrorResponse(429, $"Up to {TripLeadRules.LeadsPerTripPerDay} taxi enquiries a day for this trip.", TripEnquiryErrorTypes.LeadLimitReached);
            }
        }

        var (expiresAt, veryShortNotice) = TripExecutionRules.EnquiryExpiry(trip.StartDate!.Value, checkIn, now);
        var pushes = new List<TripPendingPush>();
        var updated = new List<Guid>();
        var counted = new List<Guid>();

        if (candidates.Count > 0 || merges.Count > 0)
        {
            var context = BuildLeadContext(trip, plan, days, checkIn, checkOut, travellerName, request.PickupTime, request.PickupPlace);
            var contextJson = JsonSerializer.Serialize(context, TripJson.Options);
            var daysJson = JsonSerializer.Serialize(days, TripJson.Options);
            var isUrgent = TripExecutionRules.IsUrgent(trip.StartDate!.Value, now);

            await unitOfWork.BeginTransactionAsync();
            try
            {
                // Parallel sends on one trip run one after the other; the per-day and per-step caps are re-checked on fresh rows.
                await TripGuards.LockTripAsync(db, trip.Id);
                var fresh = (await db.TripEnquiries.AsNoTracking()
                    .Where(x => x.TripId == trip.Id && x.Kind == TripEnquiryKinds.Taxi && TripEnquiryStatuses.Open.Contains(x.Status))
                    .ToListAsync()).Where(x => !TripSegmentRows.IsSegment(x)).ToList();
                var added = 0;
                var mergesNow = new List<(TripEnquiry Row, List<int> Days)>();
                foreach (var m in merges)
                {
                    var rowNow = fresh.FirstOrDefault(x => x.Id == m.Row.Id);
                    if (rowNow == null || rowNow.Status != m.Row.Status)
                    {
                        await unitOfWork.RollbackTransactionAsync();
                        await quotas.ReleaseAllAsync();
                        return Concurrent();
                    }
                    var heldNow = CurrentTaxiDays(m.Row, trip.StartDate!.Value);
                    if (!FitsLeadCap(fresh, m.Row.ListingId, added, DatesOf(trip.StartDate!.Value, m.Days.Except(heldNow))))
                    {
                        skipped.Add(Skip(m.Row.ListingId, TripSkipReasons.LimitReached));
                        continue;
                    }
                    mergesNow.Add(m);
                    added++;
                }
                var candidatesNow = new List<TaxiSnapshot>();
                foreach (var taxi in candidates)
                {
                    if (!FitsLeadCap(fresh, taxi.Id, added, DatesOf(trip.StartDate!.Value, days)))
                    {
                        skipped.Add(Skip(taxi.Id, TripSkipReasons.LimitReached));
                        continue;
                    }
                    candidatesNow.Add(taxi);
                    added++;
                }
                merges = mergesNow;
                candidates = candidatesNow;
                if (request.StepKey is { } capStep && candidates.Count > 0)
                {
                    var openInStep = await db.TripEnquiries.CountAsync(x => x.TripId == trip.Id && x.StepKey == capStep
                        && TripEnquiryStatuses.Open.Contains(x.Status));
                    if (!TripGuards.StepHasRoomFor(openInStep, candidates.Count))
                    {
                        await unitOfWork.RollbackTransactionAsync();
                        await quotas.ReleaseAllAsync();
                        return TripGuards.StepFullError();
                    }
                }

                // Same driver, more days: the open lead takes the union in place and the driver is told again.
                foreach (var (row, mergedDays) in merges)
                {
                    var (mergedIn, mergedOut) = TaxiSpan(trip, mergedDays);
                    var mergedContext = BuildLeadContext(trip, plan, mergedDays, mergedIn, mergedOut, travellerName,
                        string.IsNullOrWhiteSpace(request.PickupTime) ? row.PickupTime : request.PickupTime,
                        string.IsNullOrWhiteSpace(request.PickupPlace) ? row.PickupPlace : request.PickupPlace);
                    row.DaysJson = JsonSerializer.Serialize(mergedDays, TripJson.Options);
                    row.CheckIn = mergedIn;
                    row.CheckOut = mergedOut;
                    row.ContextJson = JsonSerializer.Serialize(mergedContext, TripJson.Options);
                    row.PickupTime = mergedContext.PickupTime;
                    row.PickupPlace = mergedContext.PickupPlace;
                    row.ExpiresAt = TripExecutionRules.EnquiryExpiry(trip.StartDate!.Value, mergedIn, now).ExpiresAt;
                    row.IsUrgent = isUrgent;
                    row.UpdatedAt = now;
                    if (row.Status == TripEnquiryStatuses.FareSent) TripLeadRules.ResetForMerge(row);
                    await db.SaveChangesAsync();

                    items.Add(row);
                    updated.Add(row.Id);
                    TripBulk.StagePush(db, outbox,
                        LeadDaysUpdated(row, new TripPushContext(row.OwnerUserId ?? Guid.Empty, travellerName, row.ListingName, null)),
                        row.Id, now, live, TripBulkRules.UpdateKey(row.Id, "upd", now));
                }

                var newRows = candidates.Select(taxi => (Taxi: taxi, Row: new TripEnquiry
                {
                    Id = Guid.NewGuid(),
                    TripId = trip.Id,
                    TravellerUserId = callerId,
                    OwnerUserId = taxi.UserId,
                    Kind = TripEnquiryKinds.Taxi,
                    ListingId = taxi.Id,
                    ListingName = TaxiName(taxi),
                    ListingLatitude = taxi.Latitude,
                    ListingLongitude = taxi.Longitude,
                    Day = null,
                    DaysJson = daysJson,
                    CityLeg = leg,
                    CheckIn = checkIn,
                    CheckOut = checkOut,
                    ConversationId = null,
                    ContextJson = contextJson,
                    Status = TripEnquiryStatuses.Asked,
                    ExpiresAt = expiresAt,
                    IsUrgent = isUrgent,
                    AdvanceStatus = TripAdvanceStatuses.None,
                    ListedRateSnapshot = taxi.LocalDayRate,
                    PickupTime = context.PickupTime,
                    PickupPlace = context.PickupPlace,
                    StepKey = request.StepKey,
                    ClientRequestId = staleIds.Contains(taxi.Id) ? null : request.ClientRequestId,
                    CreatedAt = now,
                    UpdatedAt = now,
                })).ToList();

                // One SaveChanges for every new row; a parallel send that took a driver answers with that row.
                var lost = await TripBulk.SaveRowsAsync(db, newRows.Select(x => x.Row).ToList(), ex => ex.InnerException is PostgresException
                    { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: OpenTaxiUniqueIndex });
                foreach (var (taxi, row) in newRows)
                {
                    if (lost.Contains(row))
                    {
                        var winner = await unitOfWork.TripEnquiries.FindOpenTaxiAsync(trip.Id, taxi.Id, leg);
                        if (winner != null) items.Add(winner);
                        else skipped.Add(Skip(taxi.Id, TripSkipReasons.AlreadyOpen));
                        continue;
                    }
                    created.Add(row.Id);
                    items.Add(row);
                    var pushContext = new TripPushContext(taxi.UserId, travellerName, row.ListingName, null);
                    TripBulk.StagePush(db, outbox, TripNotifications.TripLeadReceived(row, pushContext), row.Id, now, live);
                }

                var sentIds = newRows.Where(x => created.Contains(x.Row.Id)).Select(x => x.Taxi.Id).ToList();
                if (sentIds.Count > 0)
                {
                    counted.AddRange(sentIds);
                    await unitOfWork.TripShortlist.MarkSentAsync(trip.Id, request.StepKey, sentIds, now);
                }
                await db.SaveChangesAsync();
                await PromoteToPlannedAsync(db, trip.Id);
                await unitOfWork.CommitTransactionAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                // The driver answered the lead while its days were being merged.
                await unitOfWork.RollbackTransactionAsync();
                await quotas.ReleaseAllAsync();
                return Concurrent();
            }
            catch
            {
                await unitOfWork.RollbackTransactionAsync();
                await quotas.ReleaseAllAsync();
                throw;
            }
        }

        if (counted.Count > 0)
            await db.TaxiListings.Where(t => counted.Contains(t.Id))
                .ExecuteUpdateAsync(x => x.SetProperty(y => y.TripLeadsReceived, y => y.TripLeadsReceived + 1));

        await TripBulk.CloseUnavailableAsync(db, trip.Id, request.StepKey, skipped.Select(x => (x.ListingId, x.Reason)), now, loggers.CreateLogger("TripBulk"));

        if (updated.Count == 0 && created.Count == 0) await quotas.ReleaseAllAsync();
        outbox.Wake();
        await notifier.PublishLiveAsync(live);
        await AfterCommitAsync(chat, [], pushes, unitOfWork, notifier, loggers);

        return BatchResponse(trip, new TripEnquiryBatchDto
        {
            Items = await TripEnquiryDtoBuilder.BuildManyAsync(unitOfWork, items, callerId, now),
            Skipped = skipped,
            Updated = updated,
            VeryShortNotice = veryShortNotice,
            Results = TripBulk.BuildResults(requested, created, updated, skipped.Select(x => (x.ListingId, x.Reason)), items, request.ClientRequestId),
        }, http, replayOnly: created.Count == 0 && updated.Count == 0 && request.ClientRequestId != null && items.Count > 0 && toProcess.Count == 0);
    }

    private static NotificationEvent LeadDaysUpdated(TripEnquiry e, TripPushContext ctx)
    {
        var n = TripNotifications.TripLeadReceived(e, ctx);
        n.Title += " (updated days)";
        return n;
    }

    // The row keeps only `kept` (current day numbers of its own dates): days, span and the lead text follow.
    // An open lead gets a fresh reply window from its first kept day; a booked row's window is the caller's.
    private static void ShrinkLead(Trip trip, TripPlan plan, TripEnquiry row, List<int> kept, string travellerName, DateTime now)
    {
        var (keptIn, keptOut) = TaxiSpan(trip, kept);
        var context = BuildLeadContext(trip, plan, kept, keptIn, keptOut, travellerName, row.PickupTime, row.PickupPlace);
        row.DaysJson = JsonSerializer.Serialize(kept, TripJson.Options);
        row.CheckIn = keptIn;
        row.CheckOut = keptOut;
        row.ContextJson = JsonSerializer.Serialize(context, TripJson.Options);
        if (TripEnquiryStatuses.IsWithdrawable(row.Status))
            row.ExpiresAt = TripExecutionRules.EnquiryExpiry(trip.StartDate!.Value, keptIn, now).ExpiresAt;
        row.UpdatedAt = now;
    }

    private static string DayDates(DateOnly start, IEnumerable<int> days)
        => string.Join(", ", days.OrderBy(d => d).Select(d => TripDateText.Day(TripExecutionRules.NightDate(start, d))));

    private static Func<TripEnquiry, TripPushContext, NotificationEvent> LeadDaysRemoved(string removed) => (e, ctx) =>
    {
        var n = TripNotifications.TripBookingChanged(e, ctx);
        n.Title = (e.IsUrgent ? "Urgent · " : string.Empty) + $"{TripDateText.FirstName(ctx.TravellerName)} booked another taxi for {removed}";
        n.Body = $"You keep {TripNotifications.Dates(e)}{TripNotifications.FareSuffix(e.AgreedFare)}.";
        return n;
    };

    // ---------- POST /trips/{tripId}/taxi-bookings ("I booked") ----------

    public static async Task<IResult> CreateTaxiBooking(
        Guid tripId, CreateTaxiBookingRequest request, IValidator<CreateTaxiBookingRequest> validator, ClaimsPrincipal principal,
        IUnitOfWork unitOfWork, ApplicationDbContext db, ISavedJourneyRepository journeys, ITripChatService chat, ITripNotifier notifier,
        IRateLimitService rateLimiter, ILoggerFactory loggers, HttpContext http, IOutboxWriter outbox)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var callerId)) return UnauthorizedResponse();

        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return Invalid(validation);

        var trip = await unitOfWork.Trips.GetByIdForOwnerAsync(tripId, callerId);
        if (trip == null) return TripNotFound();
        if (TripGuards.TripNotCompleted(trip) is { } tripDone) return tripDone;

        if (trip.StartDate is not { } tripStart)
            return ErrorResponse(422, "Add your trip dates first.", TripEnquiryErrorTypes.TripDatesRequired);
        if (request.JourneyId is { } journeyId && request.JourneyLegIndex is { } carIndex)
            return await CreateSegmentBookingAsync(trip, request, journeyId, carIndex, callerId,
                unitOfWork, db, journeys, chat, notifier, rateLimiter, loggers, http, outbox);

        var now = DateTime.UtcNow;
        var requested = request.Days.Distinct().OrderBy(d => d).ToList();

        // Availability and rate are not required (the deal was made by phone); the taxi must still be approved and on this leg.
        var taxi = await LoadTaxiAsync(db, request.TaxiId);
        if (taxi == null || taxi.IsDeleted) return NotFoundResponse("Taxi not found");
        if (taxi.UserId == callerId) return BadRequestResponse("This is your own taxi.");
        if (taxi.Status != TaxiStatuses.Approved)
            return ConflictResponse("This listing is not available any more.", TripEnquiryErrorTypes.ListingNotAvailable);
        if (await IsBlockedAsync(unitOfWork, callerId, taxi.UserId))
            return ForbiddenResponse("You can't book this driver", TripSkipReasons.Blocked);

        var plan = ReadPlan(trip);
        if (requested.Count > 0 && CheckDaysInOneLeg(trip, plan, requested) is { } requestedLegError) return requestedLegError;
        if (!TripBulkRules.HostInLegArea(taxi.DistrictId, LegDistrict(trip, plan, requested.Count > 0 ? requested[0] : 1)))
            return ConflictResponse("This taxi is not in the area of these days.", TripEnquiryErrorTypes.ListingNotAvailable);
        var leg = (short)plan.LegIndexOf(requested.Count > 0 ? requested[0] : 1);
        var existing = await unitOfWork.TripEnquiries.FindOpenTaxiAsync(trip.Id, taxi.Id, leg);
        var created = existing == null;
        var wasBooked = existing != null && TripEnquiryStatuses.IsBooked(existing.Status);

        // An edit mid-trip keeps the days already travelled: they stay on the booking even when the app leaves them out.
        var keptPast = wasBooked
            ? TripLeadRules.KeptPastDays(TripEnquiryDtoBuilder.ReadDays(existing!.DaysJson), existing.CheckIn, tripStart, trip.DayCount,
                TripExecutionRules.TodayIst(now))
            : new List<int>();
        var newDays = requested.Where(d => !keptPast.Contains(d)).ToList();
        if (newDays.Count == 0) return BadRequestResponse("Pick at least one day from today on");
        if (CheckTaxiDays(trip, newDays, now) is { } dayError) return dayError;
        if (CheckDaysInOneLeg(trip, plan, newDays) is { } newLegError) return newLegError;
        var days = keptPast.Concat(newDays).Distinct().OrderBy(d => d).ToList();
        if (CheckDaysInOneLeg(trip, plan, days) is { } legError) return legError;
        var (checkIn, checkOut) = TaxiSpan(trip, days);

        if (await RateLimitedAsync(rateLimiter, http, $"trip:booking:{callerId}", TripLeadRules.BookingsPerHour, Hour) is { } limited)
            return limited;

        var travellerName = TripTextSafety.Clean(
            await db.Users.AsNoTracking().Where(u => u.Id == callerId).Select(u => u.Name).FirstOrDefaultAsync());
        var pickupTime = request.PickupTime.Trim();
        var pickupPlace = TripTextSafety.CleanOrNull(request.PickupPlace, TripLeadRules.PickupPlaceMax);
        var live = new List<NotificationEvent>();
        var replaceIds = (request.ReplaceEnquiryIds ?? new List<Guid>())
            .Take(TripLeadRules.MaxReplaceIds)
            .Append(request.ReplaceEnquiryId ?? Guid.Empty)
            .Where(x => x != Guid.Empty)
            .ToHashSet();
        TripEnquiry target;

        await unitOfWork.BeginTransactionAsync();
        try
        {
            var others = (await db.TripEnquiries
                .Where(x => x.TripId == trip.Id && x.Kind == TripEnquiryKinds.Taxi && x.ListingId != taxi.Id
                    && TripEnquiryStatuses.Open.Contains(x.Status))
                .ToListAsync())
                .Where(x => !TripSegmentRows.IsSegment(x)).ToList();
            var newDates = DatesOf(tripStart, newDays);
            var sharing = others.Where(x => SharesDate(x, newDates)).ToList();
            var booked = sharing.Where(x => TripEnquiryStatuses.IsBooked(x.Status)).ToList();

            var blocking = booked.FirstOrDefault(x => !replaceIds.Contains(x.Id));
            if (blocking != null)
            {
                await unitOfWork.RollbackTransactionAsync();
                return ErrorResponse(409, $"This day already has {blocking.ListingName}.", TripEnquiryErrorTypes.DayAlreadyBooked,
                    new TripNightConflictDto { EnquiryId = blocking.Id, ListingName = blocking.ListingName });
            }

            // A replaced driver loses only the days taken; his other days stay booked at a prorated fare.
            foreach (var replaced in booked)
            {
                var rowDays = CurrentTaxiDays(replaced, tripStart);
                var kept = TripLeadRules.RemainingDays(rowDays, newDays);
                if (kept.Count == 0)
                {
                    replaced.Status = TripEnquiryStatuses.CancelledByTraveller;
                    replaced.CancelledAt = now;
                    replaced.CancelledByUserId = callerId;
                    replaced.UpdatedAt = now;
                    await StageAsync(unitOfWork, db, outbox, replaced, replaced.OwnerUserId, TripNotifications.TripTravellerCancelled, "cancel", now, live);
                    continue;
                }
                var removed = rowDays.Except(kept).ToList();
                ShrinkLead(trip, plan, replaced, kept, travellerName, now);
                replaced.AgreedFare = TripLeadRules.ProrateFare(replaced.AgreedFare, kept.Count, Math.Max(1, rowDays.Count));
                replaced.ExpiresAt = TripExecutionRules.IstMidnightUtc(replaced.CheckOut!.Value);
                await StageAsync(unitOfWork, db, outbox, replaced, replaced.OwnerUserId, LeadDaysRemoved(DayDates(tripStart, removed)), "dayscut", now, live);
            }

            if (request.CloseOthers)
            {
                foreach (var other in sharing.Where(x => x.Status is TripEnquiryStatuses.Asked or TripEnquiryStatuses.FareSent))
                {
                    var rowDays = CurrentTaxiDays(other, tripStart);
                    var kept = TripLeadRules.RemainingDays(rowDays, newDays);
                    if (kept.Count == 0)
                    {
                        other.Status = TripEnquiryStatuses.Closed;
                        other.FailReason = TripCloseReasons.BookedElsewhere;
                        other.UpdatedAt = now;
                        continue;
                    }
                    // The fare was for the old day set, so the driver quotes again for the days left; both sides are told.
                    var removed = DayDates(tripStart, rowDays.Except(kept));
                    var hadFare = other.Status == TripEnquiryStatuses.FareSent;
                    ShrinkLead(trip, plan, other, kept, travellerName, now);
                    if (hadFare) TripLeadRules.ResetForMerge(other);
                    await StageAsync(unitOfWork, db, outbox, other, other.OwnerUserId,
                        (row, ctx) => TripNotifications.TripLeadDaysTaken(row, ctx, removed, hadFare), "daystaken", now, live);
                    if (hadFare) await StageAsync(unitOfWork, db, outbox, other, other.TravellerUserId, TripNotifications.TripFareCleared, "farecleared", now, live);
                }
            }

            var context = BuildLeadContext(trip, plan, days, checkIn, checkOut, travellerName, pickupTime, pickupPlace);

            if (existing != null)
            {
                target = existing;
            }
            else
            {
                target = new TripEnquiry
                {
                    Id = Guid.NewGuid(),
                    TripId = trip.Id,
                    TravellerUserId = callerId,
                    OwnerUserId = taxi.UserId,
                    Kind = TripEnquiryKinds.Taxi,
                    ListingId = taxi.Id,
                    ListingName = TaxiName(taxi),
                    ListingLatitude = taxi.Latitude,
                    ListingLongitude = taxi.Longitude,
                    Day = null,
                    CityLeg = leg,
                    ConversationId = null,
                    AdvanceStatus = TripAdvanceStatuses.None,
                    ListedRateSnapshot = taxi.LocalDayRate,
                    CreatedAt = now,
                };
            }

            target.Status = TripEnquiryStatuses.BookedByTraveller;
            target.FailReason = null;
            target.ConfirmedByOwnerAt = null;
            target.ConfirmedByTravellerAt ??= now;
            target.BookedByTravellerAt ??= now;
            target.DaysJson = JsonSerializer.Serialize(days, TripJson.Options);
            target.CheckIn = checkIn;
            target.CheckOut = checkOut;
            target.ContextJson = JsonSerializer.Serialize(context, TripJson.Options);
            target.AgreedFare = request.AgreedFare;
            target.TollExtra = request.TollExtra;
            target.PickupTime = pickupTime;
            target.PickupPlace = pickupPlace;
            target.ExpiresAt = TripExecutionRules.IstMidnightUtc(checkOut);
            target.IsUrgent = TripExecutionRules.IsUrgent(trip.StartDate!.Value, now);
            target.UpdatedAt = now;
            if (target.StepKey is { } bookedStep)
                await unitOfWork.TripShortlist.MarkBookedAsync(trip.Id, bookedStep, null, now);

            await StageAsync(unitOfWork, db, outbox, target, target.OwnerUserId,
                wasBooked ? TripNotifications.TripBookingChanged : TripNotifications.TripTaxiBookedByTraveller, "booked", now, live);
            if (created) db.TripEnquiries.Add(target);
            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException ex) when (created && ex.InnerException is PostgresException
                { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: OpenTaxiUniqueIndex })
            {
                await unitOfWork.RollbackTransactionAsync();
                return Concurrent();
            }
            await PromoteToPlannedAsync(db, trip.Id);
            await unitOfWork.CommitTransactionAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            await unitOfWork.RollbackTransactionAsync();
            return Concurrent();
        }
        catch
        {
            await unitOfWork.RollbackTransactionAsync();
            throw;
        }

        outbox.Wake();
        await notifier.PublishLiveAsync(live);
        await AfterCommitAsync(chat, [], [], unitOfWork, notifier, loggers);

        var dto = await TripEnquiryDtoBuilder.BuildOneAsync(unitOfWork, target, callerId, now);
        return created ? CreatedResponse(dto, $"/api/v1/trips/enquiries/{target.Id}") : OkResponse(dto);
    }

    // "I booked" for one car of a journey the trip uses: Getting there, Coming back or travel between two cities
    // (Day = DayOf(start, travel date), informational for the step roles). Only another booked driver of the same car
    // blocks; local taxis and other cars never do.
    private static async Task<IResult> CreateSegmentBookingAsync(
        Trip trip, CreateTaxiBookingRequest request, Guid journeyId, int carIndex, Guid callerId,
        IUnitOfWork unitOfWork, ApplicationDbContext db, ISavedJourneyRepository journeys, ITripChatService chat, ITripNotifier notifier,
        IRateLimitService rateLimiter, ILoggerFactory loggers, HttpContext http, IOutboxWriter outbox)
    {
        var now = DateTime.UtcNow;
        var tripStart = trip.StartDate!.Value;

        var journey = await journeys.GetOwnedAsync(journeyId, callerId, tracked: true);
        if (journey == null) return JourneyHandlers.NotFound();
        var plan = ReadPlan(trip);
        var link = journey.TripId == trip.Id ? TripJourneyLinks.Resolve(plan, journey.Id) : null;
        var cityLeg = link == null ? null : TripStepDates.LegFor(link.Role, plan, link.LegIndex);
        if (link == null || cityLeg == null || (link.Role == TripStepRoles.Segment && link.LegIndex is not >= 2))
            return ConflictResponse("Your trip is still saving. Try again.", JourneyErrorTypes.JourneyNotInPlan);
        if (TripStepDates.Expected(link.Role, plan, link.LegIndex) is not { } date)
            return ErrorResponse(422, "Add your trip dates first.", TripEnquiryErrorTypes.TripDatesRequired);
        var travelDay = (int)TripExecutionRules.DayOf(tripStart, date);

        var car = journey.Legs.FirstOrDefault(l => l.LegIndex == carIndex);
        if (car == null) return BadRequestResponse("Unknown leg");
        var requested = (request.Days ?? new List<int>()).Distinct().ToList();
        if (requested.Count > 1 || (requested.Count == 1 && requested[0] != travelDay))
            return BadRequestResponse("Journey cars travel on the travel day");
        List<int> days = [travelDay];
        if (date < TripExecutionRules.TodayIst(now))
            return ErrorResponse(422, "This day has already passed.", TripEnquiryErrorTypes.DayInPast);
        if (journey.TravelDate != date)
            return ConflictResponse("Your trip date changed. Search again.", TripEnquiryErrorTypes.TravelDateChanged);

        var taxi = await LoadTaxiAsync(db, request.TaxiId);
        if (taxi == null || taxi.IsDeleted) return NotFoundResponse("Taxi not found");
        if (taxi.UserId == callerId) return BadRequestResponse("This is your own taxi.");
        if (taxi.Status != TaxiStatuses.Approved)
            return ConflictResponse("This listing is not available any more.", TripEnquiryErrorTypes.ListingNotAvailable);
        if (await IsBlockedAsync(unitOfWork, callerId, taxi.UserId))
            return ForbiddenResponse("You can't book this driver", TripSkipReasons.Blocked);

        if (await RateLimitedAsync(rateLimiter, http, $"trip:booking:{callerId}", TripLeadRules.BookingsPerHour, Hour) is { } limited)
            return limited;

        var day = (short)travelDay;
        var existing = await unitOfWork.TripEnquiries.FindOpenAsync(trip.Id, TripEnquiryKinds.Taxi, taxi.Id, day);
        var existingKind = existing == null ? null : TripSegmentRows.Classify(existing, plan, journey.Id, carIndex);
        if (existingKind != TripSegmentRows.RowOwn
            && taxi.Id != car.DriverTaxiId && taxi.Id != car.Backup1TaxiId && taxi.Id != car.Backup2TaxiId)
            return BadRequestResponse("This driver is not on this car");
        if (existing != null && existingKind != TripSegmentRows.RowOwn && TripEnquiryStatuses.IsBooked(existing.Status))
            return ErrorResponse(409, $"{existing.ListingName} is already booked on this day.", TripEnquiryErrorTypes.DayAlreadyBooked,
                new TripNightConflictDto { EnquiryId = existing.Id, ListingName = existing.ListingName });
        if (existing != null && existingKind == TripSegmentRows.RowOther)
            return ErrorResponse(409, $"{existing.ListingName} is asked for another car on this day.", TripEnquiryErrorTypes.DayAlreadyBooked,
                new TripNightConflictDto { EnquiryId = existing.Id, ListingName = existing.ListingName });
        // A row of a replaced or removed journey gives way: it is withdrawn in the booking transaction, never taken over.
        var staleRow = existingKind == TripSegmentRows.RowStale ? existing : null;
        if (staleRow != null && !TripEnquiryStatuses.IsWithdrawable(staleRow.Status))
            return ErrorResponse(409, $"{staleRow.ListingName} is already booked on this day.", TripEnquiryErrorTypes.DayAlreadyBooked,
                new TripNightConflictDto { EnquiryId = staleRow.Id, ListingName = staleRow.ListingName });
        if (staleRow != null) existing = null;
        var created = existing == null;
        var wasBooked = existing != null && TripEnquiryStatuses.IsBooked(existing.Status);

        var travellerName = TripTextSafety.Clean(
            await db.Users.AsNoTracking().Where(u => u.Id == callerId).Select(u => u.Name).FirstOrDefaultAsync());
        var seats = await db.TaxiListings.AsNoTracking().Where(t => t.Id == taxi.Id).Select(t => t.Seats).FirstOrDefaultAsync();
        var pickupTime = request.PickupTime.Trim();
        var pickupPlace = TripTextSafety.CleanOrNull(request.PickupPlace, TripLeadRules.PickupPlaceMax);
        var comesFrom = journey.Legs.FirstOrDefault(l => l.LegIndex == carIndex - 1)?.FromName;
        var context = TripSegmentContext.Build(
            trip, plan, cityLeg, travelDay, date, journey.Id, carIndex, journey.Legs.Count,
            car.FromName, car.ToName, comesFrom, journey.People, TaxiWaysRanking.CarsNeeded(journey.People, seats),
            travellerName, pickupTime, pickupPlace, link.Role);
        var live = new List<NotificationEvent>();
        var replaceIds = (request.ReplaceEnquiryIds ?? new List<Guid>())
            .Take(TripLeadRules.MaxReplaceIds)
            .Append(request.ReplaceEnquiryId ?? Guid.Empty)
            .Where(x => x != Guid.Empty)
            .ToHashSet();
        TripEnquiry target;

        await unitOfWork.BeginTransactionAsync();
        try
        {
            var sameCar = (await db.TripEnquiries
                .Where(x => x.TripId == trip.Id && x.Kind == TripEnquiryKinds.Taxi && x.ListingId != taxi.Id && x.Day != null
                    && TripEnquiryStatuses.Open.Contains(x.Status))
                .ToListAsync())
                .Where(x => TripSegmentRows.IsSegment(x)
                    && TripEnquiryContexts.Read(x.ContextJson) is { } c && c.JourneyId == journey.Id && c.LegIndex == carIndex)
                .ToList();
            var booked = sameCar.Where(x => TripEnquiryStatuses.IsBooked(x.Status)).ToList();

            var blocking = booked.FirstOrDefault(x => !replaceIds.Contains(x.Id));
            if (blocking != null)
            {
                await unitOfWork.RollbackTransactionAsync();
                return ErrorResponse(409, $"This car already has {blocking.ListingName}.", TripEnquiryErrorTypes.DayAlreadyBooked,
                    new TripNightConflictDto { EnquiryId = blocking.Id, ListingName = blocking.ListingName });
            }

            foreach (var replaced in booked)
            {
                replaced.Status = TripEnquiryStatuses.CancelledByTraveller;
                replaced.CancelledAt = now;
                replaced.CancelledByUserId = callerId;
                replaced.UpdatedAt = now;
                await StageAsync(unitOfWork, db, outbox, replaced, replaced.OwnerUserId, TripNotifications.TripTravellerCancelled, "cancel", now, live);
            }

            if (request.CloseOthers)
            {
                foreach (var other in sameCar.Where(x => x.Status is TripEnquiryStatuses.Asked or TripEnquiryStatuses.FareSent))
                {
                    other.Status = TripEnquiryStatuses.Closed;
                    other.FailReason = TripCloseReasons.BookedElsewhere;
                    other.UpdatedAt = now;
                }
            }

            if (staleRow != null)
            {
                staleRow.Status = TripEnquiryStatuses.Closed;
                staleRow.FailReason = TripCloseReasons.Withdrawn;
                staleRow.UpdatedAt = now;
                await db.SaveChangesAsync();
                await JourneyLegSync.SyncAsync(db, staleRow, now);
                await StageAsync(unitOfWork, db, outbox, staleRow, staleRow.OwnerUserId, EnquiryWithdrawn, "wd", now, live);
            }

            if (existing != null)
            {
                target = existing;
            }
            else
            {
                target = new TripEnquiry
                {
                    Id = Guid.NewGuid(),
                    TripId = trip.Id,
                    TravellerUserId = callerId,
                    OwnerUserId = taxi.UserId,
                    Kind = TripEnquiryKinds.Taxi,
                    ListingId = taxi.Id,
                    ListingName = TaxiName(taxi),
                    ListingLatitude = taxi.Latitude,
                    ListingLongitude = taxi.Longitude,
                    Day = day,
                    ConversationId = null,
                    AdvanceStatus = TripAdvanceStatuses.None,
                    CreatedAt = now,
                };
            }

            target.Status = TripEnquiryStatuses.BookedByTraveller;
            target.FailReason = null;
            target.ConfirmedByOwnerAt = null;
            target.ConfirmedByTravellerAt ??= now;
            target.BookedByTravellerAt ??= now;
            target.DaysJson = JsonSerializer.Serialize(days, TripJson.Options);
            target.CheckIn = date;
            target.CheckOut = date.AddDays(1);
            target.ContextJson = JsonSerializer.Serialize(context, TripJson.Options);
            target.ListedRateSnapshot = car.ListedFare;
            target.AgreedFare = request.AgreedFare;
            target.TollExtra = request.TollExtra;
            target.PickupTime = pickupTime;
            target.PickupPlace = pickupPlace;
            target.ExpiresAt = TripExecutionRules.IstMidnightUtc(date.AddDays(1));
            target.IsUrgent = TripExecutionRules.IsUrgent(tripStart, now);
            target.UpdatedAt = now;
            if (target.StepKey is { } bookedStep)
                await unitOfWork.TripShortlist.MarkBookedAsync(trip.Id, bookedStep, null, now);

            await StageAsync(unitOfWork, db, outbox, target, target.OwnerUserId,
                wasBooked ? TripNotifications.TripBookingChanged : TripNotifications.TripTaxiBookedByTraveller, "booked", now, live);
            if (created) db.TripEnquiries.Add(target);
            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException ex) when (created && ex.InnerException is PostgresException
                { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: OpenUniqueIndex })
            {
                // A parallel ask or booking won this driver's travel-day row: the app retries onto it.
                await unitOfWork.RollbackTransactionAsync();
                return Concurrent();
            }

            car.LastEnquiryId = target.Id;
            car.LastKnownStatus = JourneyLegStatuses.Free;
            car.LastQuotedFare = request.AgreedFare;
            car.LastKnownAt = now;
            car.UpdatedAt = now;
            if (journey.Status != JourneyStatuses.Completed) journey.Status = JourneyRules.JourneyStatusOf(journey.Legs);
            journey.UpdatedAt = now;
            await db.SaveChangesAsync();
            await PromoteToPlannedAsync(db, trip.Id);
            await unitOfWork.CommitTransactionAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            await unitOfWork.RollbackTransactionAsync();
            return Concurrent();
        }
        catch
        {
            await unitOfWork.RollbackTransactionAsync();
            throw;
        }

        outbox.Wake();
        await notifier.PublishLiveAsync(live);
        await AfterCommitAsync(chat, [], [], unitOfWork, notifier, loggers);

        var dto = await TripEnquiryDtoBuilder.BuildOneAsync(unitOfWork, target, callerId, now);
        return created ? CreatedResponse(dto, $"/api/v1/trips/enquiries/{target.Id}") : OkResponse(dto);
    }

    // ---------- POST /trips/enquiries/{id}/lead-update ----------

    // The traveller tells the driver the day's new stops or start: the lead context is rebuilt from the saved plan.
    public static async Task<IResult> UpdateLead(
        Guid id, ClaimsPrincipal principal, IUnitOfWork unitOfWork, ApplicationDbContext db, ITripChatService chat,
        ITripNotifier notifier, ILoggerFactory loggers)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var callerId)) return UnauthorizedResponse();

        var e = await unitOfWork.TripEnquiries.GetByIdTrackedAsync(id);
        if (TripEnquiryAuth.RequireTraveller(e, callerId) is { } denied) return denied;
        if (e!.Kind != TripEnquiryKinds.Taxi || TripSegmentRows.IsSegment(e) || e.TripId is not { } tripId
            || TripEnquiryStatuses.IsTerminal(e.Status))
            return Closed("This lead is closed.");

        var trip = await unitOfWork.Trips.GetByIdForOwnerAsync(tripId, callerId);
        if (trip != null && TripGuards.TripNotCompleted(trip) is { } tripDone) return tripDone;
        if (trip?.StartDate is not { } start) return Closed("This lead is closed.");
        var days = CurrentTaxiDays(e, start).Where(d => d >= 1).ToList();
        if (days.Count == 0) return Closed("This lead is closed.");

        var now = DateTime.UtcNow;
        var old = TripEnquiryDtoBuilder.ReadContext(e.ContextJson);
        var plan = ReadPlan(trip);
        var context = BuildLeadContext(trip, plan, days,
            e.CheckIn ?? TripExecutionRules.NightDate(start, days[0]),
            e.CheckOut ?? TripExecutionRules.NightDate(start, days[^1]).AddDays(1),
            ReadTravellerName(e), e.PickupTime, e.PickupPlace);
        if (DaysKey(context.Days) != DaysKey(old.Days))
        {
            e.PickupPlace = context.PickupPlace;
            e.ContextJson = JsonSerializer.Serialize(context, TripJson.Options);
            e.UpdatedAt = now;
            try { await db.SaveChangesAsync(); }
            catch (DbUpdateConcurrencyException) { return Concurrent(); }
            await AfterCommitAsync(chat, [], [new TripPendingPush(e, e.OwnerUserId, LeadPlanUpdated)], unitOfWork, notifier, loggers);
        }
        return OkResponse(await TripEnquiryDtoBuilder.BuildOneAsync(unitOfWork, e, callerId, now));
    }

    // ---------- POST /trips/enquiries/{id}/refresh-context ----------

    public const string ContextRefreshLimitType = "ContextRefreshLimit";
    public const int MaxContextRefreshes = 3;

    // The traveller sends the host the day's current places: the stay row's context is rebuilt from the saved plan.
    // A refresh that changes nothing is free; each real one counts, three at most per row.
    public static async Task<IResult> RefreshContext(
        Guid id, ClaimsPrincipal principal, IUnitOfWork unitOfWork, ApplicationDbContext db, ITripChatService chat,
        ITripNotifier notifier, ILoggerFactory loggers)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var callerId)) return UnauthorizedResponse();

        var e = await unitOfWork.TripEnquiries.GetByIdTrackedAsync(id);
        if (TripEnquiryAuth.RequireTraveller(e, callerId) is { } denied) return denied;
        if (e!.Kind != TripEnquiryKinds.Stay || e.TripId is not { } tripId || !TripEnquiryStatuses.IsOpen(e.Status)
            || e.CheckIn is not { } checkIn || e.CheckOut is not { } checkOut)
            return Closed("This enquiry is closed.");

        var trip = await unitOfWork.Trips.GetByIdForOwnerAsync(tripId, callerId);
        if (trip != null && TripGuards.TripNotCompleted(trip) is { } tripDone) return tripDone;
        if (trip?.StartDate is not { } start) return Closed("This enquiry is closed.");

        var now = DateTime.UtcNow;
        var originalJson = e.ContextJson;
        var context = TripEnquiryDtoBuilder.ReadContext(e.ContextJson);
        var before = DaysKey(context.Days);
        context.Days = [];
        RebuildStayContext(e, context, ReadPlan(trip), start, checkIn, checkOut);
        var rebuilt = TripEnquiryDtoBuilder.ReadContext(e.ContextJson);
        if (DaysKey(rebuilt.Days) == before)
        {
            e.ContextJson = originalJson;
            db.Entry(e).State = EntityState.Unchanged;
            return OkResponse(await TripEnquiryDtoBuilder.BuildOneAsync(unitOfWork, e, callerId, now));
        }

        if (e.ContextRefreshCount >= MaxContextRefreshes)
        {
            e.ContextJson = originalJson;
            db.Entry(e).State = EntityState.Unchanged;
            return ConflictResponse("You already sent the host the new plan 3 times.", ContextRefreshLimitType);
        }

        e.ContextRefreshCount++;
        e.UpdatedAt = now;
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateConcurrencyException) { return Concurrent(); }
        await AfterCommitAsync(chat, [], [new TripPendingPush(e, e.OwnerUserId, StayPlanUpdated)], unitOfWork, notifier, loggers);
        return OkResponse(await TripEnquiryDtoBuilder.BuildOneAsync(unitOfWork, e, callerId, now));
    }

    private static NotificationEvent StayPlanUpdated(TripEnquiry e, TripPushContext ctx)
    {
        var n = TripNotifications.TripEnquiryReceived(e, ctx);
        n.Title = (e.IsUrgent ? "Urgent · " : string.Empty) + "Trip plan updated";
        n.Body = $"New places for {TripNotifications.Dates(e)}.";
        return n;
    }

    private static string DaysKey(IEnumerable<TripEnquiryContextDay> days) =>
        JsonSerializer.Serialize(days.Select(d => new { d.Day, d.Date, d.Minutes, d.Spots }), TripJson.Options);

    private static NotificationEvent LeadPlanUpdated(TripEnquiry e, TripPushContext ctx)
    {
        var n = TripNotifications.TripLeadReceived(e, ctx);
        n.Title = (e.IsUrgent ? "Urgent · " : string.Empty) + "Trip plan updated";
        n.Body = $"New stops or start for {TripNotifications.Dates(e)}.";
        return n;
    }

    // ---------- POST /trips/enquiries/{id}/withdraw ----------

    public static async Task<IResult> Withdraw(
        Guid id, ClaimsPrincipal principal, IUnitOfWork unitOfWork, ApplicationDbContext db, ITripChatService chat,
        ITripNotifier notifier, ILoggerFactory loggers)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var callerId)) return UnauthorizedResponse();

        var e = await unitOfWork.TripEnquiries.GetByIdTrackedAsync(id);
        if (TripEnquiryAuth.RequireTraveller(e, callerId) is { } denied) return denied;

        var now = DateTime.UtcNow;
        // Already closed (withdrawn, expired, declined, cancelled): nothing left to withdraw, answered as done with the row.
        if (TripEnquiryStatuses.IsTerminal(e!.Status))
            return OkResponse(await TripEnquiryDtoBuilder.BuildOneAsync(unitOfWork, e, callerId, now));
        if (!TripEnquiryStatuses.IsWithdrawable(e.Status))
            return Closed("This enquiry can no longer be withdrawn.");

        var posts = new List<TripChatPost>();
        await unitOfWork.BeginTransactionAsync();
        try
        {
            e.Status = TripEnquiryStatuses.Closed;
            e.FailReason = TripCloseReasons.Withdrawn;
            e.UpdatedAt = now;
            await TripEnquiryOps.CloseOpenOfferAsync(db, chat, e, TripOfferStatuses.Superseded, TripCardStatuses.Superseded, posts, now);
            await db.SaveChangesAsync();
            await TripEnquiryOps.PostSystemAsync(db, chat, e, callerId, TripSystemLines.Withdrawn, posts);
            await TripBulk.CloseSentRowAsync(db, e, now);
            await unitOfWork.CommitTransactionAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            await unitOfWork.RollbackTransactionAsync();
            return Concurrent();
        }
        catch
        {
            await unitOfWork.RollbackTransactionAsync();
            throw;
        }

        // The chat line alone sends no push, so the host or driver is told here.
        await AfterCommitAsync(chat, posts, [new TripPendingPush(e, e.OwnerUserId, EnquiryWithdrawn)], unitOfWork, notifier, loggers);
        return OkResponse(await TripEnquiryDtoBuilder.BuildOneAsync(unitOfWork, e, callerId, now));
    }

    // A push decided inside a booking transaction: the inbox row and the outbox row commit with it (key unique per change), so a
    // broker or FCM outage after the commit never loses it. The hub event goes out after the commit (live).
    private static async Task StageAsync(
        IUnitOfWork unitOfWork, ApplicationDbContext db, IOutboxWriter outbox, TripEnquiry e, Guid? recipient,
        Func<TripEnquiry, TripPushContext, NotificationEvent> build, string kind, DateTime now, ICollection<NotificationEvent> live)
    {
        if (recipient is not { } userId) return;
        var context = await unitOfWork.TripEnquiries.LoadPushContextAsync(e, userId);
        TripBulk.StagePush(db, outbox, build(e, context), e.Id, now, live, TripBulkRules.UpdateKey(e.Id, kind, now));
    }

    // Existing TripTravellerCancelled type and route; only the wording says withdrawn.
    internal static NotificationEvent EnquiryWithdrawn(TripEnquiry e, TripPushContext ctx)
    {
        var n = TripNotifications.TripTravellerCancelled(e, ctx);
        var who = !string.IsNullOrWhiteSpace(ctx.TravellerName) ? ctx.TravellerName.Trim()
            : e.Kind == TripEnquiryKinds.Taxi ? "A traveller" : "A guest";
        n.Title = (e.IsUrgent ? "Urgent · " : string.Empty) + "Enquiry withdrawn";
        n.Body = $"{who} withdrew the enquiry for {TripNotifications.Dates(e)}.";
        return n;
    }

    // Same type and route; the trip dates moved and this unconfirmed row fell outside them.
    internal static NotificationEvent EnquiryClosedByDateChange(TripEnquiry e, TripPushContext ctx)
    {
        var n = TripNotifications.TripTravellerCancelled(e, ctx);
        var who = string.IsNullOrWhiteSpace(ctx.TravellerName) ? "The guest" : ctx.TravellerName.Trim();
        n.Title = (e.IsUrgent ? "Urgent · " : string.Empty) + "Enquiry closed";
        n.Body = $"{who} changed the trip dates. {TripNotifications.Dates(e)} is no longer needed.";
        return n;
    }

    // Same type and route; the trip got fewer days and this unconfirmed row has a night past the new end.
    internal static NotificationEvent EnquiryClosedByDaysCut(TripEnquiry e, TripPushContext ctx)
    {
        var n = EnquiryClosedByDateChange(e, ctx);
        var who = string.IsNullOrWhiteSpace(ctx.TravellerName) ? "The guest" : ctx.TravellerName.Trim();
        n.Body = $"{who} shortened the trip. {TripNotifications.Dates(e)} is no longer needed.";
        return n;
    }

    // ---------- POST /trips/enquiries/{id}/cancel (either side) ----------

    public static async Task<IResult> Cancel(
        Guid id, CancelTripEnquiryRequest request, IValidator<CancelTripEnquiryRequest> validator, ClaimsPrincipal principal,
        IUnitOfWork unitOfWork, ApplicationDbContext db, ITripChatService chat, ITripNotifier notifier,
        IRateLimitService rateLimiter, ILoggerFactory loggers, HttpContext http)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var callerId)) return UnauthorizedResponse();

        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return Invalid(validation);

        var e = await unitOfWork.TripEnquiries.GetByIdTrackedAsync(id);
        if (TripEnquiryAuth.RequireParty(e, callerId, out var side) is { } denied) return denied;
        var byOwner = side == TripEnquiryAuth.Owner;
        if (byOwner)
        {
            if (await TripEnquiryAuth.RequireOwnerAsync(e, callerId, db) is { } notOwner) return notOwner;
            if (await RateLimitedAsync(rateLimiter, http, $"trip:owner:{callerId}", TripExecutionRules.OwnerActionsPerHour, Hour) is { } limited)
                return limited;
        }

        var now = DateTime.UtcNow;
        var cancelledStatus = byOwner ? TripEnquiryStatuses.CancelledByOwner : TripEnquiryStatuses.CancelledByTraveller;
        // Already closed by either side or by a timer: nothing left to cancel, answered as done with the row.
        if (TripEnquiryStatuses.IsTerminal(e!.Status))
            return OkResponse(await TripEnquiryDtoBuilder.BuildOneAsync(unitOfWork, e, callerId, now));
        if (!TripEnquiryStatuses.IsBooked(e.Status))
            return Closed("Only a confirmed booking can be cancelled.");
        if (e.CheckOut is { } checkOut && checkOut <= TripExecutionRules.TodayIst(now))
            return Closed(e.Kind == TripEnquiryKinds.Taxi ? "This trip has already ended." : "This stay has already ended.");

        var posts = new List<TripChatPost>();
        var pushes = new List<TripPendingPush>();
        await unitOfWork.BeginTransactionAsync();
        try
        {
            e.Status = cancelledStatus;
            e.CancelledAt = now;
            e.CancelledByUserId = callerId;
            e.CancelReason = TripTextSafety.CleanOrNull(request.Reason, TripExecutionRules.CancellationMax);
            e.UpdatedAt = now;
            await TripEnquiryOps.CloseOpenOfferAsync(db, chat, e, TripOfferStatuses.Superseded, TripCardStatuses.Superseded, posts, now);
            await TripEnquiryOps.CloseOpenAdvanceAsync(chat, e, posts);
            await db.SaveChangesAsync();
            await TripEnquiryOps.PostSystemAsync(db, chat, e, callerId,
                byOwner ? TripSystemLines.CancelledByOwner : TripSystemLines.CancelledByTraveller, posts);
            await TripBulk.CloseSentRowAsync(db, e, now);
            await unitOfWork.CommitTransactionAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            await unitOfWork.RollbackTransactionAsync();
            return Concurrent();
        }
        catch
        {
            await unitOfWork.RollbackTransactionAsync();
            throw;
        }

        pushes.Add(byOwner
            ? new TripPendingPush(e, e.TravellerUserId, TripNotifications.TripOwnerCancelled)
            : new TripPendingPush(e, e.OwnerUserId, TripNotifications.TripTravellerCancelled));
        await AfterCommitAsync(chat, posts, pushes, unitOfWork, notifier, loggers);
        return OkResponse(await TripEnquiryDtoBuilder.BuildOneAsync(unitOfWork, e, callerId, now));
    }

    // ---------- POST /trips/enquiries/{id}/offers/{offerId}/accept ----------

    public static async Task<IResult> AcceptOffer(
        Guid id, Guid offerId, AcceptTripOfferRequest request, IValidator<AcceptTripOfferRequest> validator, ClaimsPrincipal principal,
        IUnitOfWork unitOfWork, ApplicationDbContext db, ITripChatService chat, ITripNotifier notifier, ILoggerFactory loggers)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var callerId)) return UnauthorizedResponse();

        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return Invalid(validation);

        var e = await unitOfWork.TripEnquiries.GetByIdTrackedAsync(id);
        if (TripEnquiryAuth.RequireTraveller(e, callerId) is { } denied) return denied;
        var offer = await db.TripOffers.FirstOrDefaultAsync(o => o.Id == offerId && o.TripEnquiryId == id);
        if (offer == null) return ErrorResponse(404, "Offer not found", TripEnquiryErrorTypes.TripEnquiryNotFound);
        if (NotTravellerAnswerable(offer) is { } wrongKind) return wrongKind;
        if (!(offer.Status == TripOfferStatuses.Accepted && e!.AcceptedOfferId == offer.Id))
        {
            if (await TripGuards.TripNotCompletedAsync(db, e!.TripId) is { } completed) return completed;
            if (await TripGuards.ListingApprovedAsync(db, e.Kind, e.ListingId) is { } gone) return gone;
        }
        if (offer.Kind == TripOfferKinds.Revised)
            return await AcceptRevisedAsync(e!, offer, request, callerId, unitOfWork, db, chat, notifier, loggers);

        var now = DateTime.UtcNow;
        if (offer.Status == TripOfferStatuses.Accepted && e!.AcceptedOfferId == offer.Id)
            return OkResponse(await TripEnquiryDtoBuilder.BuildOneAsync(unitOfWork, e, callerId, now));
        if (TripEnquiryStatuses.IsTerminal(e!.Status) || e.TripId == null)
            return Closed("This enquiry is closed.");
        if (offer.Status == TripOfferStatuses.Expired || (offer.ValidUntil is { } validUntil && validUntil <= now))
            return ConflictResponse("This offer has expired.", TripEnquiryErrorTypes.OfferExpired);
        if (offer.Status != TripOfferStatuses.Pending || e.Status != TripEnquiryStatuses.Offered)
            return ConflictResponse("This offer is no longer open.", TripEnquiryErrorTypes.OfferNotPending);

        var checkIn = offer.CheckIn ?? e.CheckIn;
        var checkOut = offer.CheckOut ?? e.CheckOut;
        if (checkIn == null || checkOut == null) return Closed("This enquiry is closed.");
        var (tripStart, tripEnd) = await TripDatesAsync(db, e.TripId);
        if (OutsideTrip(tripStart, tripEnd, checkIn.Value, checkOut.Value) is { } outside) return outside;

        var posts = new List<TripChatPost>();
        var pushes = new List<TripPendingPush>();
        await unitOfWork.BeginTransactionAsync();
        try
        {
            if (await ApplyNightRulesAsync(db, chat, e, checkIn.Value, checkOut.Value, request.CloseOthers, request.ReplaceEnquiryId, callerId, posts, pushes, now) is { } conflict)
            {
                await unitOfWork.RollbackTransactionAsync();
                return conflict;
            }

            if (tripStart is { } planStart && (e.CheckIn != checkIn || e.CheckOut != checkOut))
                RebuildStayContext(e, TripEnquiryDtoBuilder.ReadContext(e.ContextJson), await LoadPlanAsync(db, e.TripId), planStart, checkIn.Value, checkOut.Value);
            e.Status = TripEnquiryStatuses.ConfirmedByBoth;
            e.AcceptedOfferId = offer.Id;
            e.AgreedTotal = offer.Total;
            e.CheckIn = checkIn;
            e.CheckOut = checkOut;
            if (tripStart is { } start) e.Day = TripExecutionRules.DayOf(start, checkIn.Value);
            e.ConfirmedByTravellerAt = now;
            e.ConfirmedByOwnerAt = now;
            e.UpdatedAt = now;
            offer.Status = TripOfferStatuses.Accepted;
            if (e.TripId is { } bookedTrip && e.StepKey is { } bookedStep)
                await unitOfWork.TripShortlist.MarkBookedAsync(bookedTrip, bookedStep, null, now);
            offer.RespondedAt = now;
            await db.SaveChangesAsync();

            if (offer.MessageId is { } messageId && await chat.RewriteStatusAsync(messageId, TripCardStatuses.Accepted) is { } rewrite)
                posts.Add(rewrite);
            if (e.ConversationId is { } conversationId
                && await db.Conversations.FirstOrDefaultAsync(c => c.Id == conversationId) is { } conv)
            {
                posts.Add(await chat.PostAsync(conv, callerId, TripMessageTypes.OfferResponse,
                    OfferResponsePayload(e, offer, TripCardStatuses.Accepted), "Offer accepted"));
                posts.Add(await chat.PostSystemAsync(conv, callerId, TripSystemLines.ConfirmedBoth,
                    TripSystemLines.Text(TripSystemLines.ConfirmedBoth)));
            }
            await unitOfWork.CommitTransactionAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            await unitOfWork.RollbackTransactionAsync();
            return Concurrent();
        }
        catch (DbUpdateException ex) when (IsOpenUniqueViolation(ex))
        {
            await unitOfWork.RollbackTransactionAsync();
            return Concurrent();
        }
        catch
        {
            await unitOfWork.RollbackTransactionAsync();
            throw;
        }

        pushes.Add(new TripPendingPush(e, e.OwnerUserId, TripNotifications.TripOfferAccepted, Total: offer.Total));
        await AfterCommitAsync(chat, posts, pushes, unitOfWork, notifier, loggers);
        return OkResponse(await TripEnquiryDtoBuilder.BuildOneAsync(unitOfWork, e, callerId, now));
    }

    // ---------- POST /trips/enquiries/{id}/offers/{offerId}/decline ----------

    public static async Task<IResult> DeclineOffer(
        Guid id, Guid offerId, ClaimsPrincipal principal, IUnitOfWork unitOfWork, ApplicationDbContext db,
        ITripChatService chat, ITripNotifier notifier, ILoggerFactory loggers)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var callerId)) return UnauthorizedResponse();

        var e = await unitOfWork.TripEnquiries.GetByIdTrackedAsync(id);
        if (TripEnquiryAuth.RequireTraveller(e, callerId) is { } denied) return denied;
        var offer = await db.TripOffers.FirstOrDefaultAsync(o => o.Id == offerId && o.TripEnquiryId == id);
        if (offer == null) return ErrorResponse(404, "Offer not found", TripEnquiryErrorTypes.TripEnquiryNotFound);
        if (NotTravellerAnswerable(offer) is { } wrongKind) return wrongKind;
        if (offer.Kind == TripOfferKinds.Revised)
            return await KeepRevisedAsync(e!, offer, callerId, unitOfWork, db, chat, notifier, loggers);

        var now = DateTime.UtcNow;
        if (offer.Status == TripOfferStatuses.Declined && e!.Status == TripEnquiryStatuses.Declined)
            return OkResponse(await TripEnquiryDtoBuilder.BuildOneAsync(unitOfWork, e, callerId, now));
        if (TripEnquiryStatuses.IsTerminal(e!.Status))
            return Closed("This enquiry is closed.");
        if (offer.Status != TripOfferStatuses.Pending || e.Status != TripEnquiryStatuses.Offered)
            return ConflictResponse("This offer is no longer open.", TripEnquiryErrorTypes.OfferNotPending);

        var posts = new List<TripChatPost>();
        var pushes = new List<TripPendingPush>();
        await unitOfWork.BeginTransactionAsync();
        try
        {
            e.Status = TripEnquiryStatuses.Declined;
            e.UpdatedAt = now;
            offer.Status = TripOfferStatuses.Declined;
            offer.RespondedAt = now;
            await db.SaveChangesAsync();

            if (offer.MessageId is { } messageId && await chat.RewriteStatusAsync(messageId, TripCardStatuses.Declined) is { } rewrite)
                posts.Add(rewrite);
            if (e.ConversationId is { } conversationId
                && await db.Conversations.FirstOrDefaultAsync(c => c.Id == conversationId) is { } conv)
                posts.Add(await chat.PostAsync(conv, callerId, TripMessageTypes.OfferResponse,
                    OfferResponsePayload(e, offer, TripCardStatuses.Declined), "Offer declined"));
            await TripBulk.CloseSentRowAsync(db, e, now);
            await unitOfWork.CommitTransactionAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            await unitOfWork.RollbackTransactionAsync();
            return Concurrent();
        }
        catch
        {
            await unitOfWork.RollbackTransactionAsync();
            throw;
        }

        pushes.Add(new TripPendingPush(e, e.OwnerUserId, TripNotifications.TripOfferDeclined, Total: offer.Total));
        await AfterCommitAsync(chat, posts, pushes, unitOfWork, notifier, loggers);
        return OkResponse(await TripEnquiryDtoBuilder.BuildOneAsync(unitOfWork, e, callerId, now));
    }

    // ---------- POST /trips/enquiries/{id}/counter (guest proposes a price per night) ----------

    public static async Task<IResult> Counter(
        Guid id, CounterRequest request, IValidator<CounterRequest> validator, ClaimsPrincipal principal,
        IUnitOfWork unitOfWork, ApplicationDbContext db, ITripChatService chat, ITripNotifier notifier, ILoggerFactory loggers)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var callerId)) return UnauthorizedResponse();

        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return Invalid(validation);

        var found = await unitOfWork.TripEnquiries.GetByIdTrackedAsync(id);
        if (TripEnquiryAuth.RequireTraveller(found, callerId) is { } denied) return denied;
        var e = found!;
        if (await TripGuards.TripNotCompletedAsync(db, e.TripId) is { } completed) return completed;
        if (await TripGuards.ListingApprovedAsync(db, e.Kind, e.ListingId) is { } gone) return gone;

        if (e.Kind != TripEnquiryKinds.Stay || !TripEnquiryStatuses.IsUnconfirmed(e.Status) || e.TripId == null
            || e.ConversationId is not { } conversationId)
            return ConflictResponse("You can propose a price while the enquiry is open.", TripEnquiryErrorTypes.CounterNotAllowed);

        var now = DateTime.UtcNow;
        var tripStart = await db.Trips.AsNoTracking().Where(t => t.Id == e.TripId).Select(t => t.StartDate).FirstOrDefaultAsync();
        var offers = await unitOfWork.TripOffers.GetByEnquiryAsync(e.Id);
        var openCounter = offers.FirstOrDefault(o => o.Kind == TripOfferKinds.Counter && o.Status == TripOfferStatuses.Pending);

        // The counter answers the host's latest offer: its rooms, guests and dates at the guest's per-room price.
        var context = TripEnquiryDtoBuilder.ReadContext(e.ContextJson);
        var hostOffer = offers.Where(o => o.Kind == TripOfferKinds.Offer)
            .OrderBy(o => o.Status == TripOfferStatuses.Pending ? 0 : 1)
            .ThenByDescending(o => o.CreatedAt)
            .FirstOrDefault();
        var lines = TripOfferRules.CounterLines(
            hostOffer == null ? null : TripEnquiryDtoBuilder.ReadLines(hostOffer.LinesJson),
            request.PricePerNight, context.Rooms ?? 1, (context.Adults ?? 2) + (context.Kids ?? 0));
        var counterIn = hostOffer?.CheckIn ?? e.CheckIn;
        var counterOut = hostOffer?.CheckOut ?? e.CheckOut;
        var units = lines.Sum(l => l.Units);
        var roomName = lines[0].RoomName;
        var nights = Math.Max(1, TripExecutionRules.Nights(counterIn, counterOut));
        var total = TripExecutionRules.ComputeTotal(lines, nights);

        if (openCounter != null && openCounter.Total == total && openCounter.CheckIn == counterIn && openCounter.CheckOut == counterOut)
        {
            var current = await TripEnquiryDtoBuilder.BuildOneAsync(unitOfWork, e, callerId, now);
            return OkResponse(new TripOfferResultDto { Enquiry = current, Offer = current.PendingCounter ?? new TripOfferDto { Id = openCounter.Id } });
        }
        if (offers.Count(o => o.Kind == TripOfferKinds.Counter) >= TripOfferRules.MaxCounters)
            return ConflictResponse("You can propose a price 3 times.", TripEnquiryErrorTypes.CounterLimit);
        if (total > TripExecutionRules.MaxAmount) return BadRequestResponse(TripExecutionRules.TotalRangeMessage);

        var listed = await ListedPerNightAsync(db, e.ListingId, roomName);
        var note = TripTextSafety.CleanOrNull(request.Note, TripExecutionRules.NoteMax);
        var counter = new TripOffer
        {
            Id = Guid.NewGuid(),
            TripEnquiryId = e.Id,
            Kind = TripOfferKinds.Counter,
            SenderUserId = callerId,
            CheckIn = counterIn,
            CheckOut = counterOut,
            LinesJson = JsonSerializer.Serialize(lines, TripJson.Options),
            Nights = (short)nights,
            Total = total,
            Note = note,
            ValidUntil = null,
            Status = TripOfferStatuses.Pending,
            CreatedAt = now,
        };

        var posts = new List<TripChatPost>();
        await unitOfWork.BeginTransactionAsync();
        try
        {
            foreach (var old in offers.Where(o => o.Status == TripOfferStatuses.Pending
                && (o.Kind == TripOfferKinds.Offer || o.Kind == TripOfferKinds.Counter)))
            {
                old.Status = TripOfferStatuses.Superseded;
                old.RespondedAt ??= now;
                if (old.MessageId is { } oldMessage && await chat.RewriteStatusAsync(oldMessage, TripCardStatuses.Superseded) is { } rewrite)
                    posts.Add(rewrite);
            }

            if (await db.Conversations.FirstOrDefaultAsync(c => c.Id == conversationId) is { } conversation)
            {
                var post = await chat.PostAsync(conversation, callerId, TripMessageTypes.Counter, new
                {
                    enquiryId = e.Id,
                    offerId = counter.Id,
                    pricePerNight = request.PricePerNight,
                    units,
                    nights,
                    total,
                    checkIn = IsoDate(counterIn),
                    checkOut = IsoDate(counterOut),
                    lines,
                    listedPerNight = listed,
                    note,
                    status = TripCardStatuses.Pending,
                }, $"Proposed Rs {request.PricePerNight} per room per night");
                counter.MessageId = post.Message.Id;
                posts.Add(post);
            }

            db.TripOffers.Add(counter);
            e.Status = TripEnquiryStatuses.Countered;
            // The host gets the same reply window an offer gives the guest, so the counter never expires while he decides.
            if (tripStart is { } start && e.CheckIn is { } enquiryIn)
            {
                var hostWindow = TripExecutionRules.OfferValidUntil(start, enquiryIn, now);
                if (hostWindow > e.ExpiresAt) e.ExpiresAt = hostWindow;
            }
            e.UpdatedAt = now;
            await db.SaveChangesAsync();
            await unitOfWork.CommitTransactionAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            await unitOfWork.RollbackTransactionAsync();
            return Concurrent();
        }
        catch
        {
            await unitOfWork.RollbackTransactionAsync();
            throw;
        }

        await AfterCommitAsync(chat, posts,
            [new TripPendingPush(e, e.OwnerUserId,
                (row, ctx) => TripNotifications.TripCounterReceived(row, ctx with { CheckIn = counterIn, CheckOut = counterOut }),
                Total: request.PricePerNight)],
            unitOfWork, notifier, loggers);
        var dto = await TripEnquiryDtoBuilder.BuildOneAsync(unitOfWork, e, callerId, now);
        return CreatedResponse(
            new TripOfferResultDto { Enquiry = dto, Offer = dto.PendingCounter ?? new TripOfferDto { Id = counter.Id } },
            $"/api/v1/trips/enquiries/{e.Id}");
    }

    // ---------- POST /trips/enquiries/{id}/change-request (guest; price lock until the host accepts) ----------

    public static async Task<IResult> ChangeRequest(
        Guid id, ChangeRequestRequest request, IValidator<ChangeRequestRequest> validator, ClaimsPrincipal principal,
        IUnitOfWork unitOfWork, ApplicationDbContext db, ITripChatService chat, ITripNotifier notifier, ILoggerFactory loggers)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var callerId)) return UnauthorizedResponse();

        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return Invalid(validation);

        var found = await unitOfWork.TripEnquiries.GetByIdTrackedAsync(id);
        if (TripEnquiryAuth.RequireTraveller(found, callerId) is { } denied) return denied;
        var e = found!;
        if (await TripGuards.TripNotCompletedAsync(db, e.TripId) is { } completed) return completed;
        if (await TripGuards.ListingApprovedAsync(db, e.Kind, e.ListingId) is { } gone) return gone;

        if (e.Kind != TripEnquiryKinds.Stay || e.Status != TripEnquiryStatuses.ConfirmedByBoth
            || e.AcceptedOfferId == null
            || e.ConversationId is not { } conversationId || e.TripId is not { } tripId)
            return Closed("A change needs a booking confirmed by both.");

        var now = DateTime.UtcNow;
        var trip = await db.Trips.AsNoTracking().Where(t => t.Id == tripId)
            .Select(t => new { t.StartDate, t.EndDate }).FirstOrDefaultAsync();
        if (trip?.StartDate is not { } start || trip.EndDate is not { } end) return Closed("This enquiry is closed.");
        // A stay already running keeps its past check-in; only a moved check-in or a check-out in the past is refused.
        var todayIst = TripExecutionRules.TodayIst(now);
        if ((request.CheckIn != e.CheckIn && request.CheckIn < todayIst) || request.CheckOut <= todayIst)
            return ErrorResponse(422, "That date has passed.", TripEnquiryErrorTypes.DayInPast);
        if (!TripDateRules.InsideTrip(start, end, request.CheckIn, request.CheckOut))
            return ErrorResponse(422, TripDateRules.OutsideTripMessage, TripEnquiryErrorTypes.OfferOutsideTrip);
        if (await FailedNightsBlockAsync(db, e, request.CheckIn, request.CheckOut) is { } failedNights) return failedNights;

        var offers = await unitOfWork.TripOffers.GetByEnquiryAsync(e.Id);
        if (offers.Any(o => o.Kind == TripOfferKinds.ChangeRequest && o.Status == TripOfferStatuses.Pending))
            return ConflictResponse("A change request is already waiting.", TripEnquiryErrorTypes.ChangeNotAllowed);
        var accepted = offers.FirstOrDefault(o => o.Id == e.AcceptedOfferId);
        if (accepted == null) return Closed("A change needs a booking confirmed by both.");

        var acceptedLines = TripEnquiryDtoBuilder.ReadLines(accepted.LinesJson);
        var context = TripEnquiryDtoBuilder.ReadContext(e.ContextJson);
        var oldRooms = acceptedLines.Count > 0 ? acceptedLines.Sum(l => l.Units) : context.Rooms ?? 1;
        var oldKids = context.Kids ?? 0;
        if (request.CheckIn == e.CheckIn && request.CheckOut == e.CheckOut
            && request.Adults == context.Adults && request.Kids == oldKids && request.Rooms == oldRooms)
            return ErrorResponse(422, "Change the dates, people or rooms first.", TripEnquiryErrorTypes.NothingChanged);

        var roomCaps = (await db.StayRooms.AsNoTracking()
                .Where(r => r.StayListingId == e.ListingId)
                .Select(r => new { r.RoomName, r.MaxGuests })
                .ToListAsync())
            .GroupBy(r => r.RoomName.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (int)g.Max(r => r.MaxGuests), StringComparer.OrdinalIgnoreCase);
        var lines = TripOfferRules.ChangeLines(acceptedLines, request.Rooms, request.Adults + request.Kids, roomCaps);
        if (lines == null)
            return ErrorResponse(422, "These rooms cannot seat everyone. Add a room.", TripEnquiryErrorTypes.PartyDoesNotFit);
        var nights = request.CheckOut.DayNumber - request.CheckIn.DayNumber;
        var total = TripExecutionRules.ComputeTotal(lines, nights);
        if (total < TripExecutionRules.MinAmount || total > TripExecutionRules.MaxAmount)
            return BadRequestResponse(TripExecutionRules.TotalRangeMessage);
        var moneyMoved = TripAdvanceSplit.MoneyMoved(e.AdvanceStatus);
        var changeAdvance = TripAdvanceSplit.ForTerms(accepted.AdvanceAmount, accepted.Total, total, e.AdvanceStatus, e.AdvanceAmount);
        int? refundDue = moneyMoved && e.AdvanceAmount is { } paid && paid > total ? paid - total : null;

        var note = TripTextSafety.CleanOrNull(request.Note, TripExecutionRules.NoteMax);
        var change = new TripOffer
        {
            Id = Guid.NewGuid(),
            TripEnquiryId = e.Id,
            Kind = TripOfferKinds.ChangeRequest,
            SenderUserId = callerId,
            CheckIn = request.CheckIn,
            CheckOut = request.CheckOut,
            LinesJson = JsonSerializer.Serialize(lines, TripJson.Options),
            Nights = (short)nights,
            Total = total,
            Adults = (short)request.Adults,
            Kids = (short)request.Kids,
            Note = note,
            Inclusions = accepted.Inclusions,
            AdvanceAmount = changeAdvance,
            BalanceNote = accepted.BalanceNote,
            CancellationText = accepted.CancellationText,
            GstInvoice = accepted.GstInvoice,
            ValidUntil = TripExecutionRules.OfferValidUntil(start, request.CheckIn, now),
            Status = TripOfferStatuses.Pending,
            CreatedAt = now,
        };

        var posts = new List<TripChatPost>();
        await unitOfWork.BeginTransactionAsync();
        try
        {
            foreach (var old in offers.Where(o => o.Kind == TripOfferKinds.Revised && o.Status == TripOfferStatuses.Pending))
            {
                old.Status = TripOfferStatuses.Superseded;
                old.RespondedAt ??= now;
                if (old.MessageId is { } oldMessage && await chat.RewriteStatusAsync(oldMessage, TripCardStatuses.Superseded) is { } rewrite)
                    posts.Add(rewrite);
            }

            if (await db.Conversations.FirstOrDefaultAsync(c => c.Id == conversationId) is { } conversation)
            {
                var post = await chat.PostAsync(conversation, callerId, TripMessageTypes.ChangeRequest, new
                {
                    enquiryId = e.Id,
                    offerId = change.Id,
                    oldCheckIn = IsoDate(e.CheckIn),
                    oldCheckOut = IsoDate(e.CheckOut),
                    checkIn = IsoDate(request.CheckIn),
                    checkOut = IsoDate(request.CheckOut),
                    oldAdults = context.Adults,
                    oldKids = context.Kids,
                    adults = request.Adults,
                    kids = request.Kids,
                    oldRooms,
                    rooms = request.Rooms,
                    oldTotal = e.AgreedTotal,
                    total,
                    nights,
                    lines,
                    advanceAmount = changeAdvance,
                    refundDue,
                    note,
                    status = TripCardStatuses.Pending,
                }, "Change request");
                change.MessageId = post.Message.Id;
                posts.Add(post);
            }

            db.TripOffers.Add(change);
            e.UpdatedAt = now;
            await db.SaveChangesAsync();
            await unitOfWork.CommitTransactionAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            await unitOfWork.RollbackTransactionAsync();
            return Concurrent();
        }
        catch
        {
            await unitOfWork.RollbackTransactionAsync();
            throw;
        }

        await AfterCommitAsync(chat, posts,
            [new TripPendingPush(e, e.OwnerUserId,
                (row, ctx) => TripNotifications.TripChangeRequest(
                    row, ctx with { CheckIn = request.CheckIn, CheckOut = request.CheckOut }, request.Adults, request.Kids),
                Total: total)],
            unitOfWork, notifier, loggers);
        var dto = await TripEnquiryDtoBuilder.BuildOneAsync(unitOfWork, e, callerId, now);
        return CreatedResponse(
            new TripOfferResultDto { Enquiry = dto, Offer = dto.PendingChange ?? new TripOfferDto { Id = change.Id } },
            $"/api/v1/trips/enquiries/{e.Id}");
    }

    // Stay row: the night's date is inside [CheckIn, CheckOut). Taxi row: the date is one of its chosen days' dates (D7).
    private static bool CoversDay(TripEnquiry x, int day, DateOnly date)
        => x.Kind == TripEnquiryKinds.Taxi
            ? TaxiDates(x).Contains(date)
            : x.CheckIn is { } a && x.CheckOut is { } b && a <= date && date < b;

    internal static async Task<DateOnly?> TripStartAsync(ApplicationDbContext db, Guid? tripId)
        => tripId is not { } id ? null
            : await db.Trips.AsNoTracking().Where(t => t.Id == id).Select(t => t.StartDate).FirstOrDefaultAsync();

    private static async Task<(DateOnly? Start, DateOnly? End)> TripDatesAsync(ApplicationDbContext db, Guid? tripId)
    {
        if (tripId is not { } id) return (null, null);
        var row = await db.Trips.AsNoTracking().Where(t => t.Id == id).Select(t => new { t.StartDate, t.EndDate }).FirstOrDefaultAsync();
        return (row?.StartDate, row?.EndDate);
    }

    // A dated trip only books nights inside it (check-out the morning after the last day).
    private static IResult? OutsideTrip(DateOnly? start, DateOnly? end, DateOnly checkIn, DateOnly checkOut)
        => start is { } s && end is { } t && !TripDateRules.InsideTrip(s, t, checkIn, checkOut)
            ? ErrorResponse(422, TripDateRules.OutsideTripMessage, TripEnquiryErrorTypes.OfferOutsideTrip)
            : null;

    // Nights this host could not give (a Problem split them off) are never booked back onto the same stay by a change.
    internal static async Task<IResult?> FailedNightsBlockAsync(ApplicationDbContext db, TripEnquiry e, DateOnly checkIn, DateOnly checkOut)
    {
        if (e.TripId is not { } tripId) return null;
        var failed = await db.TripEnquiries.AsNoTracking()
            .Where(x => x.TripId == tripId && x.Kind == TripEnquiryKinds.Stay && x.ListingId == e.ListingId && x.Id != e.Id
                && x.Status == TripEnquiryStatuses.Failed && x.FailReason == TripFailReasons.ProblemHostNoRoom)
            .Select(x => new { x.CheckIn, x.CheckOut })
            .ToListAsync();
        return failed.Any(x => x.CheckIn is { } a && x.CheckOut is { } b && TripExecutionRules.NightsOverlap(a, b, checkIn, checkOut))
            ? ErrorResponse(422, "The host could not give the room on some of these nights.", TripEnquiryErrorTypes.NightFailed)
            : null;
    }

    // Counters and change requests are answered by the host, never through the guest's accept / decline.
    private static IResult? NotTravellerAnswerable(TripOffer offer) => offer.Kind switch
    {
        TripOfferKinds.ChangeRequest => ConflictResponse("Only the host can answer a change request.", TripEnquiryErrorTypes.OfferNotPending),
        TripOfferKinds.Counter => ConflictResponse("This offer is no longer open.", TripEnquiryErrorTypes.OfferNotPending),
        _ => null,
    };

    // Accept new price: the only guest path that moves the snapshot (TripOfferRules.MovesSnapshot).
    private static async Task<IResult> AcceptRevisedAsync(
        TripEnquiry e, TripOffer offer, AcceptTripOfferRequest request, Guid callerId, IUnitOfWork unitOfWork,
        ApplicationDbContext db, ITripChatService chat, ITripNotifier notifier, ILoggerFactory loggers)
    {
        var now = DateTime.UtcNow;
        if (e.AcceptedOfferId == offer.Id) return OkResponse(await TripEnquiryDtoBuilder.BuildOneAsync(unitOfWork, e, callerId, now));
        if (TripEnquiryStatuses.IsTerminal(e.Status) || e.TripId == null) return Closed("This enquiry is closed.");
        if (offer.Status == TripOfferStatuses.Expired
            || (offer.Status == TripOfferStatuses.Pending && offer.ValidUntil is { } validUntil && validUntil <= now))
            return ConflictResponse("This offer has expired.", TripEnquiryErrorTypes.OfferExpired);
        if (offer.Status != TripOfferStatuses.Pending || e.Status != TripEnquiryStatuses.ConfirmedByBoth
            || offer.CheckIn is not { } checkIn || offer.CheckOut is not { } checkOut)
            return ConflictResponse("This offer is no longer open.", TripEnquiryErrorTypes.OfferNotPending);
        var (tripStart, tripEnd) = await TripDatesAsync(db, e.TripId);
        if (OutsideTrip(tripStart, tripEnd, checkIn, checkOut) is { } outside) return outside;
        if (await FailedNightsBlockAsync(db, e, checkIn, checkOut) is { } failedNights) return failedNights;

        var posts = new List<TripChatPost>();
        var pushes = new List<TripPendingPush>();
        await unitOfWork.BeginTransactionAsync();
        try
        {
            if (await ApplyNightRulesAsync(db, chat, e, checkIn, checkOut, request.CloseOthers, request.ReplaceEnquiryId, callerId, posts, pushes, now) is { } conflict)
            {
                await unitOfWork.RollbackTransactionAsync();
                return conflict;
            }

            if (e.AcceptedOfferId is { } oldId && await db.TripOffers.FirstOrDefaultAsync(o => o.Id == oldId) is { } old)
                old.Status = TripOfferStatuses.Superseded;
            // A change request was written against the old snapshot; it cannot stay open once the snapshot moves.
            var staleChanges = await db.TripOffers
                .Where(o => o.TripEnquiryId == e.Id && o.Kind == TripOfferKinds.ChangeRequest && o.Status == TripOfferStatuses.Pending)
                .ToListAsync();
            foreach (var stale in staleChanges)
            {
                stale.Status = TripOfferStatuses.Superseded;
                stale.RespondedAt ??= now;
                if (stale.MessageId is { } staleMessage && await chat.RewriteStatusAsync(staleMessage, TripCardStatuses.Superseded) is { } staleRewrite)
                    posts.Add(staleRewrite);
            }

            if (tripStart is { } planStart && (e.CheckIn != checkIn || e.CheckOut != checkOut))
                RebuildStayContext(e, TripEnquiryDtoBuilder.ReadContext(e.ContextJson), await LoadPlanAsync(db, e.TripId), planStart, checkIn, checkOut);
            offer.Status = TripOfferStatuses.Accepted;
            if (e.TripId is { } bookedTrip && e.StepKey is { } bookedStep)
                await unitOfWork.TripShortlist.MarkBookedAsync(bookedTrip, bookedStep, null, now);
            offer.RespondedAt = now;
            e.AcceptedOfferId = offer.Id;
            e.AgreedTotal = offer.Total;
            e.CheckIn = checkIn;
            e.CheckOut = checkOut;
            if (tripStart is { } start) e.Day = TripExecutionRules.DayOf(start, checkIn);
            e.UpdatedAt = now;
            await db.SaveChangesAsync();

            if (offer.MessageId is { } messageId && await chat.RewriteStatusAsync(messageId, TripCardStatuses.Accepted) is { } rewrite)
                posts.Add(rewrite);
            await TripEnquiryOps.PostSystemAsync(db, chat, e, callerId, TripSystemLines.RevisedAccepted, posts);
            await unitOfWork.CommitTransactionAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            await unitOfWork.RollbackTransactionAsync();
            return Concurrent();
        }
        catch (DbUpdateException ex) when (IsOpenUniqueViolation(ex))
        {
            await unitOfWork.RollbackTransactionAsync();
            return Concurrent();
        }
        catch
        {
            await unitOfWork.RollbackTransactionAsync();
            throw;
        }

        pushes.Add(new TripPendingPush(e, e.OwnerUserId, TripNotifications.TripOfferAccepted, Total: offer.Total));
        await AfterCommitAsync(chat, posts, pushes, unitOfWork, notifier, loggers);
        return OkResponse(await TripEnquiryDtoBuilder.BuildOneAsync(unitOfWork, e, callerId, now));
    }

    // Keep price: the revised row is declined, the booking stays exactly as it was.
    private static async Task<IResult> KeepRevisedAsync(
        TripEnquiry e, TripOffer offer, Guid callerId, IUnitOfWork unitOfWork, ApplicationDbContext db,
        ITripChatService chat, ITripNotifier notifier, ILoggerFactory loggers)
    {
        var now = DateTime.UtcNow;
        if (offer.Status == TripOfferStatuses.Declined) return OkResponse(await TripEnquiryDtoBuilder.BuildOneAsync(unitOfWork, e, callerId, now));
        if (TripEnquiryStatuses.IsTerminal(e.Status)) return Closed("This enquiry is closed.");
        if (offer.Status == TripOfferStatuses.Expired
            || (offer.Status == TripOfferStatuses.Pending && offer.ValidUntil is { } validUntil && validUntil <= now))
            return ConflictResponse("This offer has expired.", TripEnquiryErrorTypes.OfferExpired);
        if (offer.Status != TripOfferStatuses.Pending || e.Status != TripEnquiryStatuses.ConfirmedByBoth)
            return ConflictResponse("This offer is no longer open.", TripEnquiryErrorTypes.OfferNotPending);

        var posts = new List<TripChatPost>();
        await unitOfWork.BeginTransactionAsync();
        try
        {
            offer.Status = TripOfferStatuses.Declined;
            offer.RespondedAt = now;
            e.UpdatedAt = now;
            await db.SaveChangesAsync();

            if (offer.MessageId is { } messageId && await chat.RewriteStatusAsync(messageId, TripCardStatuses.Kept) is { } rewrite)
                posts.Add(rewrite);
            await TripEnquiryOps.PostSystemAsync(db, chat, e, callerId, TripSystemLines.RevisedKept, posts);
            await unitOfWork.CommitTransactionAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            await unitOfWork.RollbackTransactionAsync();
            return Concurrent();
        }
        catch
        {
            await unitOfWork.RollbackTransactionAsync();
            throw;
        }

        await AfterCommitAsync(chat, posts,
            [new TripPendingPush(e, e.OwnerUserId, TripNotifications.TripRevisedKept, Total: e.AgreedTotal)],
            unitOfWork, notifier, loggers);
        return OkResponse(await TripEnquiryDtoBuilder.BuildOneAsync(unitOfWork, e, callerId, now));
    }

    // The stay's own price of the room named on the open offer, shown next to the guest's price.
    private static async Task<int?> ListedPerNightAsync(ApplicationDbContext db, Guid stayId, string? roomName)
    {
        if (string.IsNullOrEmpty(roomName)) return null;
        return await db.StayRooms.AsNoTracking()
            .Where(r => r.StayListingId == stayId && r.RoomName == roomName)
            .Select(r => (int?)r.PricePerNight).FirstOrDefaultAsync();
    }


    // ---------- GET /trips/enquiries/{id} ----------

    public static async Task<IResult> GetOne(Guid id, ClaimsPrincipal principal, IUnitOfWork unitOfWork)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var callerId)) return UnauthorizedResponse();

        var e = await unitOfWork.TripEnquiries.GetByIdAsync(id);
        if (TripEnquiryAuth.RequireParty(e, callerId, out _) is { } denied) return denied;
        return OkResponse(await TripEnquiryDtoBuilder.BuildOneAsync(unitOfWork, e!, callerId, DateTime.UtcNow));
    }

    // ---------- GET /trips/enquiries/by-conversation/{conversationId} ----------

    public static async Task<IResult> GetByConversation(
        Guid conversationId, ClaimsPrincipal principal, IUnitOfWork unitOfWork, ApplicationDbContext db)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var callerId)) return UnauthorizedResponse();

        var isParty = await db.Conversations.AsNoTracking()
            .AnyAsync(c => c.Id == conversationId && (c.RenterId == callerId || c.OwnerId == callerId));
        if (!isParty) return TripEnquiryAuth.NotFound();

        var e = await unitOfWork.TripEnquiries.GetLatestByConversationAsync(conversationId);
        if (TripEnquiryAuth.RequireParty(e, callerId, out _) != null) return TripEnquiryAuth.NotFound();
        return OkResponse(await TripEnquiryDtoBuilder.BuildOneAsync(unitOfWork, e!, callerId, DateTime.UtcNow));
    }

    // ---------- shared ----------

    internal static async Task AfterCommitAsync(
        ITripChatService chat, List<TripChatPost> posts, IReadOnlyList<TripPendingPush> pushes, IUnitOfWork unitOfWork,
        ITripNotifier notifier, ILoggerFactory loggers)
    {
        var logger = loggers.CreateLogger("TripEnquiry");
        try { await chat.AfterCommitAsync(posts); }
        catch (Exception ex) { logger.LogError(ex, "trip.chat.broadcast_failed"); }
        await TripPushSender.SendAsync(pushes, unitOfWork.TripEnquiries, notifier, logger);
    }

    // One stay per night: a confirmed row on any overlapping night blocks (409 with data) unless it is replaceId, whose
    // overlapping nights are cancelled by the guest here (the rest of it stays booked); closeOthers closes the other
    // unconfirmed rows on those nights. The target's own stay never keeps a second open row on those nights. No mutation on 409.
    // callerId must be the guest: it is the actor of the system lines.
    internal static async Task<IResult?> ApplyNightRulesAsync(
        ApplicationDbContext db, ITripChatService chat, TripEnquiry target, DateOnly checkIn, DateOnly checkOut,
        bool closeOthers, Guid? replaceId, Guid callerId, List<TripChatPost> posts, List<TripPendingPush> pushes, DateTime now)
    {
        if (target.TripId is not { } tripId) return null;
        // Parallel accepts on one trip run one after the other; the night rows are read after the lock.
        await TripGuards.LockTripAsync(db, tripId);
        // Complete and a take-down commit under the same lock or just before it: re-check after the lock.
        if (await TripGuards.TripNotCompletedAsync(db, tripId) is { } completed) return completed;
        if (await TripGuards.ListingApprovedAsync(db, target.Kind, target.ListingId) is { } gone) return gone;
        var others = await db.TripEnquiries
            .Where(x => x.TripId == tripId && x.Kind == TripEnquiryKinds.Stay && x.Id != target.Id
                && TripEnquiryStatuses.Open.Contains(x.Status))
            .ToListAsync();
        var overlapping = others.Where(x => Overlaps(x, checkIn, checkOut)).ToList();
        var confirmed = overlapping.Where(x => TripEnquiryStatuses.IsConfirmed(x.Status)).ToList();

        var blocking = confirmed.FirstOrDefault(x => x.Id != replaceId);
        if (blocking != null)
            return ErrorResponse(409, $"This night already has {blocking.ListingName}.", TripEnquiryErrorTypes.NightAlreadyConfirmed,
                new TripNightConflictDto { EnquiryId = blocking.Id, ListingName = blocking.ListingName });

        foreach (var replaced in confirmed)
            await ReplaceNightsAsync(db, chat, replaced, checkIn, checkOut, callerId, posts, pushes, now);

        // Same stay, same nights: the row being booked takes them, so the other open ask closes (else ix_tripenquiries_open_unique).
        foreach (var same in overlapping.Where(x => x.ListingId == target.ListingId && TripEnquiryStatuses.IsUnconfirmed(x.Status)))
        {
            same.Status = TripEnquiryStatuses.Closed;
            same.FailReason = TripCloseReasons.BookedElsewhere;
            same.UpdatedAt = now;
            await TripEnquiryOps.CloseOpenOfferAsync(db, chat, same, TripOfferStatuses.Superseded, TripCardStatuses.Superseded, posts, now);
        }

        if (closeOthers)
        {
            foreach (var other in overlapping.Where(x => x.ListingId != target.ListingId && TripEnquiryStatuses.IsUnconfirmed(x.Status)))
            {
                other.Status = TripEnquiryStatuses.Closed;
                other.FailReason = TripCloseReasons.BookedElsewhere;
                other.UpdatedAt = now;
                await TripEnquiryOps.CloseOpenOfferAsync(db, chat, other, TripOfferStatuses.Superseded, TripCardStatuses.Superseded, posts, now);
                await TripEnquiryOps.PostSystemAsync(db, chat, other, callerId, TripSystemLines.BookedElsewhere, posts);
            }
        }
        // Saved before the caller moves the target's Day: the filtered unique index is checked per statement.
        await db.SaveChangesAsync();
        return null;
    }

    // Per night: only the nights [checkIn, checkOut) are cancelled; the nights before and after stay booked at their share.
    private static async Task ReplaceNightsAsync(
        ApplicationDbContext db, ITripChatService chat, TripEnquiry replaced, DateOnly checkIn, DateOnly checkOut, Guid callerId,
        List<TripChatPost> posts, List<TripPendingPush> pushes, DateTime now)
    {
        var split = TripReplaceSplit.Plan(replaced, checkIn, checkOut, callerId, now);
        if (split.Kept is not { } kept)
        {
            await TripEnquiryOps.CloseOpenAdvanceAsync(chat, replaced, posts);
            await TripEnquiryOps.PostSystemAsync(db, chat, replaced, callerId, TripSystemLines.CancelledByTraveller, posts);
            pushes.Add(new TripPendingPush(replaced, replaced.OwnerUserId, TripNotifications.TripTravellerCancelled));
            return;
        }

        var cancelled = split.Cancelled;
        db.TripEnquiries.Add(cancelled);
        if (split.KeptAfter is { } keptAfter) db.TripEnquiries.Add(keptAfter);
        // Revised offers and change requests were written for the old range: none survives the split.
        await TripEnquiryOps.CloseOpenOfferAsync(db, chat, kept, TripOfferStatuses.Superseded, TripCardStatuses.Superseded, posts, now);

        var conversation = kept.ConversationId is { } conversationId
            ? await db.Conversations.FirstOrDefaultAsync(c => c.Id == conversationId)
            : null;
        IReadOnlyList<TripEnquiry> keptRows = split.KeptAfter is { } second ? [kept, second] : [kept];
        if (kept.AcceptedOfferId is { } acceptedId && await db.TripOffers.FirstOrDefaultAsync(o => o.Id == acceptedId) is { } accepted)
        {
            accepted.Status = TripOfferStatuses.Superseded;
            if (accepted.MessageId is { } oldCard && await chat.RewriteStatusAsync(oldCard, TripCardStatuses.Superseded) is { } rewrite)
                posts.Add(rewrite);
            foreach (var row in keptRows)
            {
                var shrunk = TripProblemSplit.ShrinkAcceptedOffer(accepted, row, now);
                if (conversation != null)
                {
                    var card = await chat.PostAsync(conversation, shrunk.SenderUserId, TripMessageTypes.Offer,
                        KeptOfferCardPayload(row, shrunk), $"Offer · Rs {shrunk.Total}");
                    shrunk.MessageId = card.Message.Id;
                    posts.Add(card);
                }
                db.TripOffers.Add(shrunk);
                row.AcceptedOfferId = shrunk.Id;
            }
        }

        var cut = TripReplaceSplit.NightsText(cancelled.CheckIn, cancelled.CheckOut);
        var keep = string.Join(" and ", keptRows.Select(r => TripReplaceSplit.NightsText(r.CheckIn, r.CheckOut)));
        if (conversation != null && cancelled.TravellerUserId is { } guestId)
            posts.Add(await chat.PostSystemAsync(conversation, guestId, TripSystemLines.CancelledByTraveller,
                $"The guest cancelled {cut}. The other nights stay booked."));
        pushes.Add(new TripPendingPush(cancelled, cancelled.OwnerUserId, NightsCancelled(cut, keep)));
    }

    private static Func<TripEnquiry, TripPushContext, NotificationEvent> NightsCancelled(string cut, string keep) => (e, ctx) =>
    {
        var n = TripNotifications.TripTravellerCancelled(e, ctx);
        n.Title = "Booking changed";
        n.Body = $"{TripDateText.FirstName(ctx.TravellerName)} cancelled {cut}. You keep {keep}.";
        return n;
    };

    private static object KeptOfferCardPayload(TripEnquiry kept, TripOffer shrunk) => new
    {
        enquiryId = kept.Id,
        offerId = shrunk.Id,
        checkIn = shrunk.CheckIn,
        checkOut = shrunk.CheckOut,
        nights = (int)shrunk.Nights,
        lines = TripEnquiryDtoBuilder.ReadLines(shrunk.LinesJson),
        total = shrunk.Total,
        listedPerNight = (int?)null,
        inclusions = shrunk.Inclusions,
        advanceAmount = shrunk.AdvanceAmount,
        balanceNote = shrunk.BalanceNote,
        cancellationText = shrunk.CancellationText,
        gstInvoice = shrunk.GstInvoice,
        validUntil = shrunk.ValidUntil,
        status = TripCardStatuses.Accepted,
        datesDiffer = false,
    };

    // The stay row's context covers exactly [checkIn, checkOut): known days stay, new nights come from the plan, the text follows.
    public static void RebuildStayContext(
        TripEnquiry e, TripEnquiryContext context, TripPlan? plan, DateOnly start, DateOnly checkIn, DateOnly checkOut)
    {
        var days = new List<TripEnquiryContextDay>();
        for (var date = checkIn; date < checkOut; date = date.AddDays(1))
        {
            if (context.Days.FirstOrDefault(d => d.Date == date) is { } known)
            {
                days.Add(known);
                continue;
            }
            var day = (int)TripExecutionRules.DayOf(start, date);
            var planDay = plan?.Days?.FirstOrDefault(x => x.Day == day);
            days.Add(new TripEnquiryContextDay
            {
                Day = day,
                Date = date,
                Minutes = planDay?.Spots.Sum(s => s.VisitMinutes) ?? 0,
                Spots = planDay?.Spots
                    .Select(s => TripTextSafety.Clean(s.Name))
                    .Where(n => n.Length > 0)
                    .ToList() ?? new List<string>(),
            });
        }
        context.Days = days;
        if (context.CityCount > 1 && plan?.Days is { Count: > 0 } planDays) context.TripDayCount = planDays.Count;
        context.Text = TripEnquiryText.Build(context, checkIn, checkOut, checkOut.DayNumber - checkIn.DayNumber > 1);
        e.ContextJson = JsonSerializer.Serialize(context, TripJson.Options);
    }

    // An ask or a booking makes a Draft trip Planned, so reminders and auto-complete see it. Never touches the tracked Trip.
    internal static Task<int> PromoteToPlannedAsync(ApplicationDbContext db, Guid tripId)
        => db.Trips.Where(t => t.Id == tripId && t.Status == TripStatuses.Draft)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, TripStatuses.Planned));

    // A racing write took the same stay and night (ix_tripenquiries_open_unique): answered as a concurrent change, never a 500.
    internal static bool IsOpenUniqueViolation(DbUpdateException ex)
        => ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: OpenUniqueIndex };

    internal static async Task<TripPlan?> LoadPlanAsync(ApplicationDbContext db, Guid? tripId)
    {
        if (tripId is not { } id) return null;
        var json = await db.Trips.AsNoTracking().Where(t => t.Id == id).Select(t => t.PlanJson).FirstOrDefaultAsync();
        if (string.IsNullOrWhiteSpace(json)) return null;
        var plan = TripPlanReader.Parse(json);
        plan.Normalize();
        return plan;
    }

    private static IResult? CheckNight(Trip trip, int day, DateTime now)
    {
        if (trip.StartDate is not { } start)
            return ErrorResponse(422, "Add your trip dates first.", TripEnquiryErrorTypes.TripDatesRequired);
        if (day < 1 || day > trip.DayCount) return BadRequestResponse("This day has no night to book");
        if (TripExecutionRules.NightDate(start, day) < TripExecutionRules.TodayIst(now))
            return ErrorResponse(422, "This day has already passed.", TripEnquiryErrorTypes.DayInPast);
        return null;
    }

    // Every day has a night. All nights = this night through the leg's last night (check-out the next leg's travel-day morning,
    // or the morning after the trip's last day).
    private static (DateOnly CheckIn, DateOnly CheckOut) Nights(Trip trip, TripPlan plan, int day, bool allNights, int? nights = null)
    {
        var start = trip.StartDate!.Value;
        var checkIn = TripExecutionRules.NightDate(start, day);
        var checkOut = TripLegRules.StayCheckOut(plan, start, day, allNights, nights);
        return (checkIn, checkOut);
    }

    // A taxi row serves one city: every chosen day must be in the same leg.
    private static IResult? CheckDaysInOneLeg(Trip trip, TripPlan plan, IEnumerable<int> days)
    {
        var inTrip = days.Where(d => d >= 1 && d <= trip.DayCount).Distinct().OrderBy(d => d).ToList();
        return TripLegRules.DaysInOneLeg(plan, inTrip)
            ? null
            : BadRequestResponse("Pick days in one city", TripEnquiryErrorTypes.DaysAcrossCities);
    }

    // Same stay only inside one leg: an Asked row takes new nights only when the joined nights stay in one city.
    private static bool JoinStaysInOneLeg(Trip trip, TripPlan plan, TripEnquiry row, DateOnly checkIn, DateOnly checkOut)
    {
        if (row.CheckIn is not { } a || row.CheckOut is not { } b) return false;
        var start = trip.StartDate!.Value;
        var first = a < checkIn ? a : checkIn;
        var last = b > checkOut ? b : checkOut;
        var nights = new List<int>();
        for (var d = first; d < last; d = d.AddDays(1))
        {
            var day = d.DayNumber - start.DayNumber + 1;
            if (day >= 1 && day <= trip.DayCount) nights.Add(day);
        }
        return TripLegRules.DaysInOneLeg(plan, nights);
    }

    private static bool Overlaps(TripEnquiry x, DateOnly checkIn, DateOnly checkOut)
        => x.CheckIn is { } a && x.CheckOut is { } b && TripExecutionRules.NightsOverlap(a, b, checkIn, checkOut);

    // Taxi days: dates set, every day inside the trip and not in the past.
    private static IResult? CheckTaxiDays(Trip trip, IReadOnlyList<int> days, DateTime now)
    {
        if (trip.StartDate is not { } start)
            return ErrorResponse(422, "Add your trip dates first.", TripEnquiryErrorTypes.TripDatesRequired);
        if (days.Count == 0) return BadRequestResponse("Pick at least one day");
        foreach (var d in days)
            if (d < 1 || d > trip.DayCount) return BadRequestResponse($"This trip has no day {d}");
        var today = TripExecutionRules.TodayIst(now);
        if (days.Any(d => TripExecutionRules.NightDate(start, d) < today))
            return ErrorResponse(422, "This day has already passed.", TripEnquiryErrorTypes.DayInPast);
        return null;
    }

    // CheckIn = first chosen day, CheckOut = last chosen day + 1, so overlap, urgency and expiry reuse the stay rules.
    private static (DateOnly CheckIn, DateOnly CheckOut) TaxiSpan(Trip trip, IReadOnlyList<int> days)
    {
        var start = trip.StartDate!.Value;
        return (TripExecutionRules.NightDate(start, days.Min()), TripExecutionRules.NightDate(start, days.Max()).AddDays(1));
    }

    // D7: a taxi row covers {CheckIn + (d - min(days))}, so it keeps its dates when the trip start moves.
    public static List<DateOnly> TaxiDates(TripEnquiry x)
    {
        if (x.CheckIn is not { } first) return [];
        if (TripEnquiryDtoBuilder.ReadDays(x.DaysJson) is { Length: > 0 } days)
        {
            var min = days.Min();
            return days.Distinct().Order().Select(d => first.AddDays(d - min)).ToList();
        }
        var dates = new List<DateOnly>();
        var end = x.CheckOut is { } last && last > first ? last : first.AddDays(1);
        for (var d = first; d < end; d = d.AddDays(1)) dates.Add(d);
        return dates;
    }

    // The row's dates as day numbers of the current trip start (may fall outside 1..DayCount after a start move).
    private static List<int> CurrentTaxiDays(TripEnquiry x, DateOnly start)
        => TaxiDates(x).Select(d => (int)TripExecutionRules.DayOf(start, d)).Distinct().OrderBy(d => d).ToList();

    private static List<DateOnly> DatesOf(DateOnly start, IEnumerable<int> days)
        => days.Distinct().OrderBy(d => d).Select(d => TripExecutionRules.NightDate(start, d)).ToList();

    private static bool SharesDate(TripEnquiry x, IReadOnlyCollection<DateOnly> dates) => TaxiDates(x).Any(dates.Contains);

    // At most MaxOpenLeadsPerDay open taxi rows on every chosen date, this taxi's own rows not counted.
    private static bool FitsLeadCap(List<TripEnquiry> tripOpen, Guid listingId, int alreadyAdded, IReadOnlyList<DateOnly> dates)
    {
        foreach (var date in dates)
        {
            var count = tripOpen.Count(x => x.ListingId != listingId && SharesDate(x, [date])) + alreadyAdded;
            if (count >= TripLeadRules.MaxOpenLeadsPerDay) return false;
        }
        return true;
    }

    // Lead context from the trip row: chosen days only; pickup time and place from the request only.
    private static TripEnquiryContext BuildLeadContext(
        Trip trip, TripPlan plan, IReadOnlyList<int> chosen, DateOnly checkIn, DateOnly checkOut, string travellerName,
        string? pickupTime, string? pickupPlace)
    {
        var settings = plan.Settings ?? new TripSettings();
        var days = chosen.Select(d =>
        {
            var planDay = plan.Days?.FirstOrDefault(x => x.Day == d);
            return new TripEnquiryContextDay
            {
                Day = d,
                Date = TripExecutionRules.NightDate(trip.StartDate!.Value, d),
                Minutes = planDay?.Spots.Sum(s => s.VisitMinutes) ?? 0,
                Spots = planDay?.Spots
                    .Select(s => TripTextSafety.Clean(s.Name))
                    .Where(n => n.Length > 0)
                    .ToList() ?? new List<string>(),
            };
        }).ToList();

        var time = !string.IsNullOrWhiteSpace(pickupTime) ? pickupTime.Trim() : null;
        if (time != null && !System.Text.RegularExpressions.Regex.IsMatch(time, TripLeadRules.PickupTimePattern)) time = null;
        var place = TripTextSafety.CleanOrNull(pickupPlace, TripLeadRules.PickupPlaceMax);

        var context = new TripEnquiryContext
        {
            TripTitle = null,
            Adults = settings.Adults,
            Kids = settings.Kids,
            Rooms = null,
            TravellerName = travellerName.Length == 0 ? null : travellerName,
            Days = days,
            PickupTime = time,
            PickupPlace = place,
        };
        ApplyCity(context, trip, plan, chosen.Count == 0 ? 1 : chosen.Min());
        context.Text = TripEnquiryText.BuildLead(context, checkIn, checkOut);
        return context;
    }

    private sealed record TaxiSnapshot(
        Guid Id, Guid UserId, string? DriverName, string? VehicleTypeName, string Status, bool IsAvailable, bool IsDeleted,
        int? LocalDayRate, double? Latitude, double? Longitude, Guid? DistrictId);

    // One query for every requested taxi.
    private static async Task<Dictionary<Guid, TaxiSnapshot>> LoadTaxisAsync(ApplicationDbContext db, IReadOnlyCollection<Guid> taxiIds)
        => taxiIds.Count == 0
            ? []
            : (await db.TaxiListings.AsNoTracking()
                .Where(t => taxiIds.Contains(t.Id))
                .Select(t => new TaxiSnapshot(
                    t.Id, t.UserId, t.User.Name, t.VehicleType != null ? t.VehicleType.Name : null, t.Status, t.IsAvailable, t.IsDeleted,
                    t.LocalDayRate,
                    t.Latitude == null ? null : (double?)t.Latitude, t.Longitude == null ? null : (double?)t.Longitude, t.DistrictId))
                .ToListAsync()).ToDictionary(x => x.Id);

    private static async Task<TaxiSnapshot?> LoadTaxiAsync(ApplicationDbContext db, Guid taxiId)
        => await db.TaxiListings.AsNoTracking()
            .Where(t => t.Id == taxiId)
            .Select(t => new TaxiSnapshot(
                t.Id, t.UserId, t.User.Name, t.VehicleType != null ? t.VehicleType.Name : null, t.Status, t.IsAvailable, t.IsDeleted,
                t.LocalDayRate,
                t.Latitude == null ? null : (double?)t.Latitude, t.Longitude == null ? null : (double?)t.Longitude, t.DistrictId))
            .FirstOrDefaultAsync();

    // The driver's name, as the taxi browse cards show it.
    private static string TaxiName(TaxiSnapshot taxi) => TripBulk.TaxiLeadName(taxi.DriverName, taxi.VehicleTypeName);

    // The district of the city leg a day is in (the trip's own district for a single-city plan); null = unknown.
    public static Guid? LegDistrict(Trip trip, TripPlan plan, int day)
        => plan.LegOf(day) is { } leg ? leg.DistrictId : trip.DistrictId;

    private static TripEnquiryContext BuildContext(
        Trip trip, TripPlan plan, int firstDay, DateOnly checkIn, DateOnly checkOut, bool allNights, string travellerName)
    {
        var settings = plan.Settings ?? new TripSettings();
        var nights = Math.Max(1, checkOut.DayNumber - checkIn.DayNumber);
        var days = new List<TripEnquiryContextDay>();
        for (var d = firstDay; d < firstDay + nights; d++)
        {
            var planDay = plan.Days?.FirstOrDefault(x => x.Day == d);
            days.Add(new TripEnquiryContextDay
            {
                Day = d,
                Date = TripExecutionRules.NightDate(trip.StartDate!.Value, d),
                Minutes = planDay?.Spots.Sum(s => s.VisitMinutes) ?? 0,
                Spots = planDay?.Spots
                    .Select(s => TripTextSafety.Clean(s.Name))
                    .Where(n => n.Length > 0)
                    .ToList() ?? new List<string>(),
            });
        }

        var context = new TripEnquiryContext
        {
            TripTitle = null,
            Adults = settings.Adults,
            Kids = settings.Kids,
            Rooms = null,
            TravellerName = travellerName.Length == 0 ? null : travellerName,
            Days = days,
        };
        ApplyCity(context, trip, plan, firstDay);
        context.Text = TripEnquiryText.Build(context, checkIn, checkOut, allNights);
        return context;
    }

    private static void ApplyCity(TripEnquiryContext context, Trip trip, TripPlan plan, int day)
        => TripEnquiryCity.Apply(context, plan, day, trip.CityName, trip.DayCount);

    private static TripPlan ReadPlan(Trip trip) => TripPlanReader.Read(trip);

    private sealed record StaySnapshot(
        Guid Id, Guid UserId, string? Name, string Status, bool AcceptingGuests, bool IsDeleted, double? Latitude, double? Longitude,
        Guid? StayTypeId, string? CityName, string? DistrictName, Guid? DistrictId);

    // One query for every requested stay.
    private static async Task<Dictionary<Guid, StaySnapshot>> LoadStaysAsync(ApplicationDbContext db, IReadOnlyCollection<Guid> stayIds)
        => stayIds.Count == 0
            ? []
            : (await db.StayListings.AsNoTracking()
                .Where(s => stayIds.Contains(s.Id))
                .Select(s => new StaySnapshot(
                    s.Id, s.UserId, s.Name, s.Status, s.AcceptingGuests, s.IsDeleted,
                    s.Latitude == null ? null : (double?)s.Latitude, s.Longitude == null ? null : (double?)s.Longitude,
                    s.StayTypeId, s.City != null ? s.City.Name : null, s.District != null ? s.District.Name : null, s.DistrictId))
                .ToListAsync()).ToDictionary(x => x.Id);

    private static string ListingName(StaySnapshot stay)
    {
        var name = TripTextSafety.Clean(stay.Name, 120);
        return name.Length == 0 ? "Stay" : name;
    }

    private static async Task<bool> IsBlockedAsync(IUnitOfWork unitOfWork, Guid a, Guid b)
        => await unitOfWork.UserBlocks.ExistsAsync(a, b) || await unitOfWork.UserBlocks.ExistsAsync(b, a);

    private static TripSkippedDto Skip(Guid listingId, string reason) => new() { ListingId = listingId, Reason = reason };

    // One count per request on both keys, held by the caller's lease (a daily denial gives the hourly permit back);
    // asksHeader adds X-Trip-Asks-Left (hourly asks left) to the answer, 2xx or 429.
    private static async Task<IResult?> EnquiryRateLimitedAsync(RateLimitPermits lease, HttpContext http, Guid userId, bool asksHeader = false)
    {
        var hourly = await lease.TakeAsync($"trip:enquiry:h:{userId}", TripExecutionRules.EnquiriesPerHour, Hour);
        if (asksHeader) http.Response.Headers[TripExecutionRules.AsksLeftHeader] = Math.Max(0, hourly.RemainingAttempts).ToString();
        if (!hourly.IsAllowed) return EnquiryLimited(http, hourly.RetryAfter ?? Hour);

        var daily = await lease.TakeAsync($"trip:enquiry:d:{userId}", TripExecutionRules.EnquiriesPerDay, Day24);
        return daily.IsAllowed ? null : EnquiryLimited(http, daily.RetryAfter ?? Day24);
    }

    private static IResult EnquiryLimited(HttpContext http, TimeSpan retryAfter)
    {
        http.Response.Headers["Retry-After"] = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
        return ErrorResponse(429, $"Up to {TripExecutionRules.EnquiriesPerHour} asks an hour and {TripExecutionRules.EnquiriesPerDay} a day.",
            TripEnquiryErrorTypes.EnquiryLimitReached);
    }

    internal static async Task<IResult?> RateLimitedAsync(
        IRateLimitService rateLimiter, HttpContext http, string key, int max, TimeSpan window)
    {
        var rl = await rateLimiter.CheckAsync(key, max, window);
        if (rl.IsAllowed) return null;
        http.Response.Headers["Retry-After"] = ((int)(rl.RetryAfter ?? window).TotalSeconds).ToString();
        return TooManyRequestsResponse();
    }

    private static string IsoDate(DateOnly? d) => d?.ToString("yyyy-MM-dd") ?? string.Empty;

    private static string ReadTravellerName(TripEnquiry e)
        => TripEnquiryDtoBuilder.ReadContext(e.ContextJson).TravellerName ?? string.Empty;

    private static object OfferResponsePayload(TripEnquiry e, TripOffer offer, string answer) => new
    {
        enquiryId = e.Id,
        offerId = offer.Id,
        answer,
        total = offer.Total,
        checkIn = IsoDate(offer.CheckIn ?? e.CheckIn),
        checkOut = IsoDate(offer.CheckOut ?? e.CheckOut),
    };
}
