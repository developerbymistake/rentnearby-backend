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
        ITripNotifier notifier, IRateLimitService rateLimiter, IMemoryCache cache, ILoggerFactory loggers, HttpContext http)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var callerId)) return UnauthorizedResponse();

        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return Invalid(validation);

        var trip = await unitOfWork.Trips.GetByIdForOwnerAsync(tripId, callerId);
        if (trip == null) return TripNotFound();

        return await CreateCoreAsync(trip, request, callerId, [], unitOfWork, db, conversations, chat, notifier, rateLimiter, cache, loggers, http);
    }

    // The whole send path of Create after the trip is loaded; Ask again reuses it. preSkipped rows lead the Skipped list.
    private static async Task<IResult> CreateCoreAsync(
        Trip trip, CreateTripEnquiryRequest request, Guid callerId, IReadOnlyList<TripSkippedDto> preSkipped,
        IUnitOfWork unitOfWork, ApplicationDbContext db, IConversationFactory conversations, ITripChatService chat,
        ITripNotifier notifier, IRateLimitService rateLimiter, IMemoryCache cache, ILoggerFactory loggers, HttpContext http)
    {
        if (request.Kind == TripEnquiryKinds.Taxi)
            return await CreateLeadsAsync(trip, request, callerId, preSkipped, unitOfWork, db, notifier, chat, rateLimiter, cache, loggers, http);

        var now = DateTime.UtcNow;
        if (CheckNight(trip, request.Day, now) is { } nightError) return nightError;
        var (checkIn, checkOut) = Nights(trip, request.Day, request.AllNights, request.Nights);
        var multiNight = request.Nights != null ? TripExecutionRules.Nights(checkIn, checkOut) > 1 : request.AllNights;

        var tabs = await ConfigHandlers.GetAppTabsCachedAsync(unitOfWork, cache);
        if (!TabState.IsKindActive(tabs, ListingKinds.Stay))
            return ForbiddenResponse("Stays are not available right now.", TabGateFilter.ErrorType);

        if (await EnquiryRateLimitedAsync(rateLimiter, http, callerId, asksHeader: true) is { } limited) return limited;

        var travellerName = TripTextSafety.Clean(
            await db.Users.AsNoTracking().Where(u => u.Id == callerId).Select(u => u.Name).FirstOrDefaultAsync());

        var tripOpen = (await unitOfWork.TripEnquiries.GetOpenByTripAsync(trip.Id, TripEnquiryKinds.Stay)).ToList();
        var items = new List<TripEnquiry>();
        var skipped = new List<TripSkippedDto>(preSkipped);
        var candidates = new List<StaySnapshot>();
        var stayMerges = new List<TripEnquiry>();
        var day = (short)request.Day;

        foreach (var listingId in request.ListingIds)
        {
            var stay = await LoadStayAsync(db, listingId);
            if (stay == null || stay.IsDeleted || stay.Status != StayStatuses.Approved || !stay.AcceptingGuests || stay.UserId == callerId)
            {
                skipped.Add(Skip(listingId, TripSkipReasons.NotAvailable));
                continue;
            }
            if (await IsBlockedAsync(unitOfWork, callerId, stay.UserId))
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
                && (Adjacent(unconfirmed, checkIn, checkOut) || Overlaps(unconfirmed, checkIn, checkOut));

            var mine = await unitOfWork.TripEnquiries.GetOpenByTravellerListingAsync(callerId, TripEnquiryKinds.Stay, listingId);
            if (mine.Any(x => Overlaps(x, checkIn, checkOut) && !(joinable && x.Id == unconfirmed!.Id)))
            {
                skipped.Add(Skip(listingId, TripSkipReasons.AlreadyOpen));
                continue;
            }

            if (unconfirmed != null)
            {
                if (joinable && FitsNightCap(tripOpen, listingId, candidates.Count + stayMerges.Count, checkIn, checkOut))
                    stayMerges.Add(unconfirmed);
                else
                    skipped.Add(Skip(listingId, TripSkipReasons.AlreadyOpen));
                continue;
            }

            if (!FitsNightCap(tripOpen, listingId, candidates.Count + stayMerges.Count, checkIn, checkOut))
            {
                skipped.Add(Skip(listingId, TripSkipReasons.LimitReached));
                continue;
            }
            candidates.Add(stay);
        }

        var (expiresAt, veryShortNotice) = TripExecutionRules.EnquiryExpiry(trip.StartDate!.Value, checkIn, now);
        var posts = new List<TripChatPost>();
        var pushes = new List<TripPendingPush>();
        var updated = new List<Guid>();

        if (candidates.Count > 0 || stayMerges.Count > 0)
        {
            var plan = ReadPlan(trip);
            var context = BuildContext(trip, plan, request.Day, checkIn, checkOut, multiNight, travellerName);
            var contextJson = JsonSerializer.Serialize(context, TripJson.Options);
            var isUrgent = TripExecutionRules.IsUrgent(trip.StartDate!.Value, now);

            await unitOfWork.BeginTransactionAsync();
            try
            {
                // Next to an Asked row of the same stay: the row takes the joined nights and the host is told again.
                foreach (var row in stayMerges)
                {
                    var start = trip.StartDate!.Value;
                    var mergedIn = row.CheckIn!.Value < checkIn ? row.CheckIn.Value : checkIn;
                    var mergedOut = row.CheckOut!.Value > checkOut ? row.CheckOut.Value : checkOut;
                    var mergedDay = mergedIn.DayNumber - start.DayNumber + 1;
                    var allNights = mergedOut == Nights(trip, mergedDay, allNights: true).CheckOut;
                    var mergedContext = BuildContext(trip, plan, mergedDay, mergedIn, mergedOut, allNights, travellerName);
                    row.Day = (short)mergedDay;
                    row.CheckIn = mergedIn;
                    row.CheckOut = mergedOut;
                    row.ContextJson = JsonSerializer.Serialize(mergedContext, TripJson.Options);
                    row.ExpiresAt = TripExecutionRules.EnquiryExpiry(start, mergedIn, now).ExpiresAt;
                    row.IsUrgent = isUrgent;
                    row.UpdatedAt = now;
                    await db.SaveChangesAsync();

                    if (row.ConversationId is { } rowConversationId
                        && await db.Conversations.FirstOrDefaultAsync(c => c.Id == rowConversationId) is { } rowConversation)
                        posts.Add(await chat.PostAsync(rowConversation, callerId, TripMessageTypes.Enquiry,
                            EnquiryCardPayload(row, mergedContext), mergedContext.Text));

                    items.Add(row);
                    updated.Add(row.Id);
                    pushes.Add(new TripPendingPush(row, row.OwnerUserId, EnquiryNightsUpdated));
                }

                foreach (var stay in candidates)
                {
                    var conv = await conversations.GetOrCreateAsync(callerId, ListingKinds.Stay, stay.Id, skipRateLimit: true);
                    if (conv.Conversation == null || conv.Status is not (200 or 201))
                    {
                        if (conv.Status is 400 or 404 or 409)
                        {
                            skipped.Add(Skip(stay.Id, TripSkipReasons.NotAvailable));
                            continue;
                        }
                        await unitOfWork.RollbackTransactionAsync();
                        return ErrorResponse(conv.Status, conv.Message ?? "Could not start the chat.", conv.ErrorType ?? "BadRequest");
                    }

                    var e = new TripEnquiry
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
                        CreatedAt = now,
                        UpdatedAt = now,
                    };
                    db.TripEnquiries.Add(e);
                    try
                    {
                        await db.SaveChangesAsync();
                    }
                    catch (DbUpdateException ex) when (ex.InnerException is PostgresException
                        { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: OpenUniqueIndex })
                    {
                        // A parallel send won the same night: answer with that row.
                        db.Entry(e).State = EntityState.Detached;
                        var winner = await unitOfWork.TripEnquiries.FindOpenAsync(trip.Id, TripEnquiryKinds.Stay, stay.Id, day);
                        if (winner != null) items.Add(winner);
                        else skipped.Add(Skip(stay.Id, TripSkipReasons.AlreadyOpen));
                        continue;
                    }

                    posts.Add(await chat.PostAsync(conv.Conversation, callerId, TripMessageTypes.Enquiry,
                        EnquiryCardPayload(e, context), context.Text));

                    await db.StayListings.Where(s => s.Id == stay.Id)
                        .ExecuteUpdateAsync(s => s.SetProperty(x => x.TripEnquiriesReceived, x => x.TripEnquiriesReceived + 1));

                    items.Add(e);
                    pushes.Add(new TripPendingPush(e, e.OwnerUserId, TripNotifications.TripEnquiryReceived));
                }
                await unitOfWork.CommitTransactionAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                // The host answered the enquiry while its nights were being joined.
                await unitOfWork.RollbackTransactionAsync();
                return Concurrent();
            }
            catch
            {
                await unitOfWork.RollbackTransactionAsync();
                throw;
            }
        }

        await AfterCommitAsync(chat, posts, pushes, unitOfWork, notifier, loggers);

        return CreatedResponse(new TripEnquiryBatchDto
        {
            Items = await TripEnquiryDtoBuilder.BuildManyAsync(unitOfWork, items, callerId, now),
            Skipped = skipped,
            Updated = updated,
            VeryShortNotice = veryShortNotice,
        }, $"/api/v1/trips/{trip.Id}");
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
        needs = context.Needs,
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
        HttpContext http)
    {
        var now = DateTime.UtcNow;
        var days = (request.Days ?? []).Distinct().OrderBy(d => d).ToList();
        if (CheckTaxiDays(trip, days, now) is { } dayError) return dayError;
        var (checkIn, checkOut) = TaxiSpan(trip, days);

        var tabs = await ConfigHandlers.GetAppTabsCachedAsync(unitOfWork, cache);
        if (!TabState.IsKindActive(tabs, ListingKinds.Taxi))
            return ForbiddenResponse("Taxis are not available right now.", TabGateFilter.ErrorType);

        var travellerName = TripTextSafety.Clean(
            await db.Users.AsNoTracking().Where(u => u.Id == callerId).Select(u => u.Name).FirstOrDefaultAsync());

        var tripOpen = (await unitOfWork.TripEnquiries.GetOpenByTripAsync(trip.Id, TripEnquiryKinds.Taxi)).ToList();
        var items = new List<TripEnquiry>();
        var skipped = new List<TripSkippedDto>(preSkipped);
        var candidates = new List<TaxiSnapshot>();
        var merges = new List<(TripEnquiry Row, List<int> Days)>();

        foreach (var listingId in request.ListingIds)
        {
            var taxi = await LoadTaxiAsync(db, listingId);
            if (taxi == null || taxi.IsDeleted || taxi.Status != TaxiStatuses.Approved || !taxi.IsAvailable
                || taxi.LocalDayRate == null || taxi.UserId == callerId)
            {
                skipped.Add(Skip(listingId, TripSkipReasons.NotAvailable));
                continue;
            }
            if (await IsBlockedAsync(unitOfWork, callerId, taxi.UserId))
            {
                skipped.Add(Skip(listingId, TripSkipReasons.Blocked));
                continue;
            }

            // Journey leads (no trip) are a separate kind: the same driver can be the journey car and the local taxi.
            var mine = (await unitOfWork.TripEnquiries.GetOpenByTravellerListingAsync(callerId, TripEnquiryKinds.Taxi, listingId))
                .Where(x => x.TripId != null).ToList();

            var same = tripOpen.FirstOrDefault(x => x.ListingId == listingId && x.Day == null);
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
                if (mine.Any(x => x.Id != same.Id && Overlaps(x, mergedIn, mergedOut)))
                {
                    skipped.Add(Skip(listingId, TripSkipReasons.AlreadyOpen));
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

            if (mine.Any(x => Overlaps(x, checkIn, checkOut)))
            {
                skipped.Add(Skip(listingId, TripSkipReasons.AlreadyOpen));
                continue;
            }

            if (!FitsLeadCap(tripOpen, listingId, candidates.Count + merges.Count, DatesOf(trip.StartDate!.Value, days)))
            {
                skipped.Add(Skip(listingId, TripSkipReasons.LimitReached));
                continue;
            }
            candidates.Add(taxi);
        }

        // Only a request that sends or widens a lead spends the ask and lead quotas.
        if (candidates.Count > 0 || merges.Count > 0)
        {
            if (await EnquiryRateLimitedAsync(rateLimiter, http, callerId, asksHeader: true) is { } limited) return limited;
            var leadLimit = await rateLimiter.CheckAsync($"trip:lead:{trip.Id}", TripLeadRules.LeadsPerTripPerDay, Day24);
            if (!leadLimit.IsAllowed)
            {
                http.Response.Headers["Retry-After"] = ((int)Math.Ceiling((leadLimit.RetryAfter ?? Day24).TotalSeconds)).ToString();
                return ErrorResponse(429, $"Up to {TripLeadRules.LeadsPerTripPerDay} taxi enquiries a day for this trip.", TripEnquiryErrorTypes.LeadLimitReached);
            }
        }

        var (expiresAt, veryShortNotice) = TripExecutionRules.EnquiryExpiry(trip.StartDate!.Value, checkIn, now);
        var pushes = new List<TripPendingPush>();
        var updated = new List<Guid>();

        if (candidates.Count > 0 || merges.Count > 0)
        {
            var plan = ReadPlan(trip);
            var context = BuildLeadContext(trip, plan, days, checkIn, checkOut, travellerName, request.PickupTime, request.PickupPlace);
            var contextJson = JsonSerializer.Serialize(context, TripJson.Options);
            var daysJson = JsonSerializer.Serialize(days, TripJson.Options);
            var isUrgent = TripExecutionRules.IsUrgent(trip.StartDate!.Value, now);

            await unitOfWork.BeginTransactionAsync();
            try
            {
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
                    pushes.Add(new TripPendingPush(row, row.OwnerUserId, LeadDaysUpdated));
                }

                foreach (var taxi in candidates)
                {
                    var e = new TripEnquiry
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
                        CreatedAt = now,
                        UpdatedAt = now,
                    };
                    db.TripEnquiries.Add(e);
                    try
                    {
                        await db.SaveChangesAsync();
                    }
                    catch (DbUpdateException ex) when (ex.InnerException is PostgresException
                        { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: OpenTaxiUniqueIndex })
                    {
                        // A parallel send won this taxi: answer with that row.
                        db.Entry(e).State = EntityState.Detached;
                        var winner = await unitOfWork.TripEnquiries.FindOpenAsync(trip.Id, TripEnquiryKinds.Taxi, taxi.Id, null);
                        if (winner != null) items.Add(winner);
                        else skipped.Add(Skip(taxi.Id, TripSkipReasons.AlreadyOpen));
                        continue;
                    }

                    await db.TaxiListings.Where(t => t.Id == taxi.Id)
                        .ExecuteUpdateAsync(s => s.SetProperty(x => x.TripLeadsReceived, x => x.TripLeadsReceived + 1));

                    items.Add(e);
                    pushes.Add(new TripPendingPush(e, e.OwnerUserId, TripNotifications.TripLeadReceived));
                }
                await unitOfWork.CommitTransactionAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                // The driver answered the lead while its days were being merged.
                await unitOfWork.RollbackTransactionAsync();
                return Concurrent();
            }
            catch
            {
                await unitOfWork.RollbackTransactionAsync();
                throw;
            }
        }

        await AfterCommitAsync(chat, [], pushes, unitOfWork, notifier, loggers);

        return CreatedResponse(new TripEnquiryBatchDto
        {
            Items = await TripEnquiryDtoBuilder.BuildManyAsync(unitOfWork, items, callerId, now),
            Skipped = skipped,
            Updated = updated,
            VeryShortNotice = veryShortNotice,
        }, $"/api/v1/trips/{trip.Id}");
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
        n.Body = $"You keep {TripNotifications.Dates(e)}, Rs {(e.AgreedFare ?? 0).ToString("N0", System.Globalization.CultureInfo.InvariantCulture)}.";
        return n;
    };

    // ---------- POST /trips/{tripId}/taxi-bookings ("I booked") ----------

    public static async Task<IResult> CreateTaxiBooking(
        Guid tripId, CreateTaxiBookingRequest request, IValidator<CreateTaxiBookingRequest> validator, ClaimsPrincipal principal,
        IUnitOfWork unitOfWork, ApplicationDbContext db, ITripChatService chat, ITripNotifier notifier,
        IRateLimitService rateLimiter, ILoggerFactory loggers, HttpContext http)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var callerId)) return UnauthorizedResponse();

        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return Invalid(validation);

        var trip = await unitOfWork.Trips.GetByIdForOwnerAsync(tripId, callerId);
        if (trip == null) return TripNotFound();

        var now = DateTime.UtcNow;
        var requested = request.Days.Distinct().OrderBy(d => d).ToList();
        if (trip.StartDate is not { } tripStart)
            return ErrorResponse(422, "Add your trip dates first.", TripEnquiryErrorTypes.TripDatesRequired);

        // Approval, availability and rate are not required: the deal was made by phone.
        var taxi = await LoadTaxiAsync(db, request.TaxiId);
        if (taxi == null || taxi.IsDeleted) return NotFoundResponse("Taxi not found");
        if (taxi.UserId == callerId) return BadRequestResponse("This is your own taxi.");
        if (await IsBlockedAsync(unitOfWork, callerId, taxi.UserId))
            return ForbiddenResponse("You can't book this driver", TripSkipReasons.Blocked);

        var existing = await unitOfWork.TripEnquiries.FindOpenAsync(trip.Id, TripEnquiryKinds.Taxi, taxi.Id, null);
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
        var days = keptPast.Concat(newDays).Distinct().OrderBy(d => d).ToList();
        var (checkIn, checkOut) = TaxiSpan(trip, days);

        if (await RateLimitedAsync(rateLimiter, http, $"trip:booking:{callerId}", TripLeadRules.BookingsPerHour, Hour) is { } limited)
            return limited;

        var travellerName = TripTextSafety.Clean(
            await db.Users.AsNoTracking().Where(u => u.Id == callerId).Select(u => u.Name).FirstOrDefaultAsync());
        var pickupTime = request.PickupTime.Trim();
        var pickupPlace = TripTextSafety.Clean(request.PickupPlace, TripLeadRules.PickupPlaceMax);
        var pushes = new List<TripPendingPush>();
        var replaceIds = (request.ReplaceEnquiryIds ?? new List<Guid>())
            .Take(TripLeadRules.MaxReplaceIds)
            .Append(request.ReplaceEnquiryId ?? Guid.Empty)
            .Where(x => x != Guid.Empty)
            .ToHashSet();
        TripEnquiry target;

        await unitOfWork.BeginTransactionAsync();
        try
        {
            var others = await db.TripEnquiries
                .Where(x => x.TripId == trip.Id && x.Kind == TripEnquiryKinds.Taxi && x.ListingId != taxi.Id
                    && TripEnquiryStatuses.Open.Contains(x.Status))
                .ToListAsync();
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

            var plan = ReadPlan(trip);

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
                    pushes.Add(new TripPendingPush(replaced, replaced.OwnerUserId, TripNotifications.TripTravellerCancelled));
                    continue;
                }
                var removed = rowDays.Except(kept).ToList();
                ShrinkLead(trip, plan, replaced, kept, travellerName, now);
                replaced.AgreedFare = TripLeadRules.ProrateFare(replaced.AgreedFare, kept.Count, Math.Max(1, rowDays.Count));
                replaced.ExpiresAt = TripExecutionRules.IstMidnightUtc(replaced.CheckOut!.Value);
                pushes.Add(new TripPendingPush(replaced, replaced.OwnerUserId, LeadDaysRemoved(DayDates(tripStart, removed))));
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
                    pushes.Add(new TripPendingPush(other, other.OwnerUserId,
                        (row, ctx) => TripNotifications.TripLeadDaysTaken(row, ctx, removed, hadFare)));
                    if (hadFare) pushes.Add(new TripPendingPush(other, other.TravellerUserId, TripNotifications.TripFareCleared));
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

        pushes.Add(new TripPendingPush(target, target.OwnerUserId,
            wasBooked ? TripNotifications.TripBookingChanged : TripNotifications.TripTaxiBookedByTraveller));
        await AfterCommitAsync(chat, [], pushes, unitOfWork, notifier, loggers);

        var dto = await TripEnquiryDtoBuilder.BuildOneAsync(unitOfWork, target, callerId, now);
        return created ? CreatedResponse(dto, $"/api/v1/trips/enquiries/{target.Id}") : OkResponse(dto);
    }

    // ---------- POST /trips/{tripId}/phone-bookings ----------

    public static async Task<IResult> CreatePhoneBooking(
        Guid tripId, CreatePhoneBookingRequest request, IValidator<CreatePhoneBookingRequest> validator, ClaimsPrincipal principal,
        IUnitOfWork unitOfWork, ApplicationDbContext db, ITripChatService chat, ITripNotifier notifier,
        IRateLimitService rateLimiter, IOptions<TripExecutionOptions> options, ILoggerFactory loggers, HttpContext http)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var callerId)) return UnauthorizedResponse();

        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return Invalid(validation);

        var trip = await unitOfWork.Trips.GetByIdForOwnerAsync(tripId, callerId);
        if (trip == null) return TripNotFound();

        var now = DateTime.UtcNow;
        if (CheckNight(trip, request.Day, now) is { } nightError) return nightError;
        var (checkIn, checkOut) = Nights(trip, request.Day, request.AllNights, request.Nights);
        var multiNight = request.Nights != null ? TripExecutionRules.Nights(checkIn, checkOut) > 1 : request.AllNights;

        if (await EnquiryRateLimitedAsync(rateLimiter, http, callerId) is { } limited) return limited;
        if (await RateLimitedAsync(rateLimiter, http, $"trip:phonebooking:{callerId}", TripExecutionRules.PhoneBookingsPerDay, Day24) is { } pbLimited)
            return pbLimited;

        if (await unitOfWork.TripEnquiries.CountNotMeAsync(callerId, now.AddDays(-TripExecutionRules.NotMeWindowDays))
            >= TripExecutionRules.NotMePauseCount)
            return ErrorResponse(429, "Phone bookings are paused for 30 days.", TripEnquiryErrorTypes.PhoneBookingPaused);

        // Paused or unapproved stays are allowed: the deal was made by phone.
        var stay = await LoadStayAsync(db, request.StayId);
        if (stay == null || stay.IsDeleted) return NotFoundResponse("Stay not found");
        if (stay.UserId == callerId) return BadRequestResponse("This is your own stay.");
        if (await IsBlockedAsync(unitOfWork, callerId, stay.UserId))
            return ForbiddenResponse("You can't book with this host.", TripSkipReasons.Blocked);

        var day = (short)request.Day;
        var existing = await unitOfWork.TripEnquiries.FindOpenAsync(trip.Id, TripEnquiryKinds.Stay, stay.Id, day);
        if (existing?.Status == TripEnquiryStatuses.ConfirmedByBoth)
            return Closed("Already confirmed by both. Cancel it first.");

        var travellerName = TripTextSafety.Clean(
            await db.Users.AsNoTracking().Where(u => u.Id == callerId).Select(u => u.Name).FirstOrDefaultAsync());
        var fields = Fields(request);
        var posts = new List<TripChatPost>();
        var pushes = new List<TripPendingPush>();
        string? token = null;
        TripEnquiry target;
        var created = false;

        await unitOfWork.BeginTransactionAsync();
        try
        {
            if (existing?.Status == TripEnquiryStatuses.ConfirmedByTraveller)
            {
                // Same as PUT .../phone-booking: an edit, no new link.
                if (await ApplyNightRulesAsync(db, chat, existing, checkIn, checkOut, closeOthers: false, replaceId: null, callerId, posts, pushes, now) is { } phoneConflict)
                {
                    await unitOfWork.RollbackTransactionAsync();
                    return phoneConflict;
                }
                ApplyPhoneFields(existing, fields, checkIn, checkOut, now, keepRecordedAt: true);
                await db.SaveChangesAsync();
                if (await RewritePhoneCardAsync(db, existing, TripCardStatuses.Pending) is { } rewrite) posts.Add(rewrite);
                await db.SaveChangesAsync();
                await unitOfWork.CommitTransactionAsync();
                await AfterCommitAsync(chat, posts, pushes, unitOfWork, notifier, loggers);
                return OkResponse(await PhoneResultAsync(unitOfWork, existing, null, callerId, now, options.Value, travellerName));
            }

            if (existing != null)
            {
                target = existing;
            }
            else
            {
                var conversation = await unitOfWork.Conversations.FindExistingAsync(callerId, stay.UserId, ListingKinds.Stay, stay.Id);
                var plan = ReadPlan(trip);
                var context = BuildContext(trip, plan, request.Day, checkIn, checkOut, multiNight, travellerName);
                target = new TripEnquiry
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
                    ConversationId = conversation?.Id,
                    ContextJson = JsonSerializer.Serialize(context, TripJson.Options),
                    AdvanceStatus = TripAdvanceStatuses.None,
                    IsUrgent = TripExecutionRules.IsUrgent(trip.StartDate!.Value, now),
                    CreatedAt = now,
                };
                created = true;
            }

            if (await ApplyNightRulesAsync(db, chat, target, checkIn, checkOut, request.CloseOthers, request.ReplaceEnquiryId, callerId, posts, pushes, now) is { } conflict)
            {
                await unitOfWork.RollbackTransactionAsync();
                return conflict;
            }

            if (!created)
                await TripEnquiryOps.CloseOpenOfferAsync(db, chat, target, TripOfferStatuses.Superseded, TripCardStatuses.Superseded, posts, now);

            target.Status = TripEnquiryStatuses.ConfirmedByTraveller;
            target.FailReason = null;
            target.ConfirmedByTravellerAt = now;
            target.ConfirmedByOwnerAt = null;
            ApplyPhoneFields(target, fields, checkIn, checkOut, now, keepRecordedAt: false);
            target.ExpiresAt = TripExecutionRules.HostLinkExpiry(checkIn, now);

            token = HostConfirmTokens.Generate();
            target.HostConfirmTokenHash = HostConfirmTokens.Hash(token);
            target.HostConfirmTokenIssuedAt = now;
            target.HostConfirmTokenExpiresAt = TripExecutionRules.HostLinkExpiry(checkIn, now);
            target.HostConfirmLinkCount = 1;
            target.HostConfirmAnsweredAt = null;
            target.HostConfirmAnswer = null;
            target.HostConfirmAnsweredVia = null;
            target.HostConfirmAnsweredByUserId = null;

            if (created) db.TripEnquiries.Add(target);
            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException ex) when (created && ex.InnerException is PostgresException
                { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: OpenUniqueIndex })
            {
                await unitOfWork.RollbackTransactionAsync();
                return Concurrent();
            }

            if (target.ConversationId is { } conversationId
                && await db.Conversations.FirstOrDefaultAsync(c => c.Id == conversationId) is { } conv)
            {
                posts.Add(await chat.PostAsync(conv, callerId, TripMessageTypes.PhoneBooking,
                    PhoneCardPayload(target, ReadBooking(target)!, TripCardStatuses.Pending), "Booking recorded by phone"));
            }

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

        pushes.Add(new TripPendingPush(target, target.OwnerUserId, TripNotifications.TripPhoneBookingRecorded, Total: target.AgreedTotal));
        await AfterCommitAsync(chat, posts, pushes, unitOfWork, notifier, loggers);

        var result = await PhoneResultAsync(unitOfWork, target, token, callerId, now, options.Value, travellerName);
        return CreatedResponse(result, $"/api/v1/trips/enquiries/{target.Id}");
    }

    // ---------- PUT /trips/enquiries/{id}/phone-booking ----------

    public static async Task<IResult> EditPhoneBooking(
        Guid id, EditPhoneBookingRequest request, IValidator<EditPhoneBookingRequest> validator, ClaimsPrincipal principal,
        IUnitOfWork unitOfWork, ApplicationDbContext db, ITripChatService chat, ITripNotifier notifier,
        IOptions<TripExecutionOptions> options, ILoggerFactory loggers)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var callerId)) return UnauthorizedResponse();

        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return Invalid(validation);

        var e = await unitOfWork.TripEnquiries.GetByIdTrackedAsync(id);
        if (TripEnquiryAuth.RequireTraveller(e, callerId) is { } denied) return denied;
        if (e!.Status != TripEnquiryStatuses.ConfirmedByTraveller || e.TripId is not { } tripId)
            return Closed("This booking can no longer be edited.");

        var trip = await unitOfWork.Trips.GetByIdForOwnerAsync(tripId, callerId);
        if (trip?.StartDate is not { } start || trip.EndDate is not { } end || e.CheckIn is not { } checkIn || e.CheckOut is not { } currentOut)
            return Closed("This booking can no longer be edited.");

        // The check-in never moves (D7); only the length changes, capped at the trip's check-out morning.
        var left = TripDateRules.CheckOutLimit(end).DayNumber - checkIn.DayNumber;
        if (checkIn < start || left < 1)
            return ErrorResponse(422, TripDateRules.OutsideTripMessage, TripEnquiryErrorTypes.OfferOutsideTrip);
        var current = Math.Max(1, currentOut.DayNumber - checkIn.DayNumber);
        var nights = request.Nights ?? (request.AllNights ? left : current);
        var checkOut = checkIn.AddDays(Math.Clamp(nights, 1, left));

        var now = DateTime.UtcNow;
        var posts = new List<TripChatPost>();
        var pushes = new List<TripPendingPush>();
        await unitOfWork.BeginTransactionAsync();
        try
        {
            if (await ApplyNightRulesAsync(db, chat, e, checkIn, checkOut, closeOthers: false, replaceId: null, callerId, posts, pushes, now) is { } conflict)
            {
                await unitOfWork.RollbackTransactionAsync();
                return conflict;
            }
            e.Day = (short)(checkIn.DayNumber - start.DayNumber + 1);
            ApplyPhoneFields(e, Fields(request), checkIn, checkOut, now, keepRecordedAt: true);
            await db.SaveChangesAsync();
            if (await RewritePhoneCardAsync(db, e, TripCardStatuses.Pending) is { } rewrite) posts.Add(rewrite);
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

        await AfterCommitAsync(chat, posts, pushes, unitOfWork, notifier, loggers);
        var travellerName = ReadTravellerName(e);
        return OkResponse(await PhoneResultAsync(unitOfWork, e, null, callerId, now, options.Value, travellerName));
    }

    // ---------- POST /trips/enquiries/{id}/host-confirm-link/resend ----------

    // Only the hash is stored, so a resend issues a fresh token; the older link stops working.
    public static async Task<IResult> ResendHostLink(
        Guid id, ClaimsPrincipal principal, IUnitOfWork unitOfWork, IOptions<TripExecutionOptions> options)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var callerId)) return UnauthorizedResponse();

        var e = await unitOfWork.TripEnquiries.GetByIdTrackedAsync(id);
        if (TripEnquiryAuth.RequireTraveller(e, callerId) is { } denied) return denied;
        if (e!.Status != TripEnquiryStatuses.ConfirmedByTraveller || e.HostConfirmAnsweredAt != null || e.HostConfirmTokenHash == null)
            return Closed("The host already answered.");
        if (e.HostConfirmLinkCount >= TripExecutionRules.HostLinkMaxCount)
            return ConflictResponse("The link was already sent twice.", TripEnquiryErrorTypes.HostLinkLimit);

        var now = DateTime.UtcNow;
        var retryAt = (e.HostConfirmTokenIssuedAt ?? now).AddHours(TripExecutionRules.HostLinkResendHours);
        if (now < retryAt)
            return ErrorResponse(429, "You can resend the link after 24 hours.", TripEnquiryErrorTypes.HostLinkResendTooSoon, new { retryAt });

        var token = HostConfirmTokens.Generate();
        e.HostConfirmTokenHash = HostConfirmTokens.Hash(token);
        e.HostConfirmTokenExpiresAt = e.CheckIn is { } checkIn ? TripExecutionRules.HostLinkExpiry(checkIn, now) : now.AddDays(TripExecutionRules.HostLinkDays);
        e.HostConfirmLinkCount++;
        e.UpdatedAt = now;

        try { await unitOfWork.SaveChangesAsync(); }
        catch (DbUpdateConcurrencyException) { return Concurrent(); }

        return OkResponse(await PhoneResultAsync(unitOfWork, e, token, callerId, now, options.Value, ReadTravellerName(e)));
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
        if (e!.Status == TripEnquiryStatuses.Closed && e.FailReason == TripCloseReasons.Withdrawn)
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
        if (e!.Status == cancelledStatus)
            return OkResponse(await TripEnquiryDtoBuilder.BuildOneAsync(unitOfWork, e, callerId, now));
        if (!TripEnquiryStatuses.IsBooked(e.Status))
            return Closed("Only a confirmed booking can be cancelled.");
        if (byOwner && e.CheckOut is { } checkOut && checkOut <= TripExecutionRules.TodayIst(now))
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
            await db.SaveChangesAsync();
            await TripEnquiryOps.PostSystemAsync(db, chat, e, callerId,
                byOwner ? TripSystemLines.CancelledByOwner : TripSystemLines.CancelledByTraveller, posts);
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

        if (e.Kind != TripEnquiryKinds.Stay || e.Status != TripEnquiryStatuses.ConfirmedByBoth || e.AcceptedOfferId is not { } acceptedId
            || e.ConversationId is not { } conversationId || e.TripId is not { } tripId)
            return Closed("A change needs a booking confirmed by both.");

        var now = DateTime.UtcNow;
        var trip = await db.Trips.AsNoTracking().Where(t => t.Id == tripId)
            .Select(t => new { t.StartDate, t.EndDate }).FirstOrDefaultAsync();
        if (trip?.StartDate is not { } start || trip.EndDate is not { } end) return Closed("This enquiry is closed.");
        if (request.CheckIn < TripExecutionRules.TodayIst(now))
            return ErrorResponse(422, "That date has passed.", TripEnquiryErrorTypes.DayInPast);
        if (!TripDateRules.InsideTrip(start, end, request.CheckIn, request.CheckOut))
            return ErrorResponse(422, TripDateRules.OutsideTripMessage, TripEnquiryErrorTypes.OfferOutsideTrip);
        if (await FailedNightsBlockAsync(db, e, request.CheckIn, request.CheckOut) is { } failedNights) return failedNights;

        var offers = await unitOfWork.TripOffers.GetByEnquiryAsync(e.Id);
        if (offers.Any(o => o.Kind == TripOfferKinds.ChangeRequest && o.Status == TripOfferStatuses.Pending))
            return ConflictResponse("A change request is already waiting.", TripEnquiryErrorTypes.ChangeNotAllowed);
        var accepted = offers.FirstOrDefault(o => o.Id == acceptedId);
        if (accepted == null) return Closed("A change needs a booking confirmed by both.");

        var acceptedLines = TripEnquiryDtoBuilder.ReadLines(accepted.LinesJson);
        var context = TripEnquiryDtoBuilder.ReadContext(e.ContextJson);
        var oldRooms = acceptedLines.Count > 0 ? acceptedLines.Sum(l => l.Units) : context.Rooms ?? 1;
        var oldKids = context.Kids ?? 0;
        if (request.CheckIn == e.CheckIn && request.CheckOut == e.CheckOut
            && request.Adults == context.Adults && request.Kids == oldKids && request.Rooms == oldRooms)
            return ErrorResponse(422, "Change the dates, people or rooms first.", TripEnquiryErrorTypes.NothingChanged);

        var lines = TripOfferRules.ChangeLines(acceptedLines, request.Rooms, request.Adults + request.Kids);
        var nights = request.CheckOut.DayNumber - request.CheckIn.DayNumber;
        var total = TripExecutionRules.ComputeTotal(lines, nights);
        if (total < TripExecutionRules.MinAmount || total > TripExecutionRules.MaxAmount)
            return BadRequestResponse(TripExecutionRules.TotalRangeMessage);

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
            NeedsEchoedJson = accepted.NeedsEchoedJson,
            AdvanceAmount = accepted.AdvanceAmount,
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

    // ---------- POST /trips/{tripId}/enquiries/ask-again (Plan B: the day's shortlist, through the Create path) ----------

    public static async Task<IResult> AskAgain(
        Guid tripId, AskAgainRequest request, IValidator<AskAgainRequest> validator, ClaimsPrincipal principal,
        IUnitOfWork unitOfWork, ApplicationDbContext db, IConversationFactory conversations, ITripChatService chat,
        ITripNotifier notifier, IRateLimitService rateLimiter, IMemoryCache cache, ILoggerFactory loggers, HttpContext http)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var callerId)) return UnauthorizedResponse();

        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return Invalid(validation);

        var trip = await unitOfWork.Trips.GetByIdForOwnerAsync(tripId, callerId);
        if (trip == null) return TripNotFound();
        if (trip.StartDate is not { } start)
            return ErrorResponse(422, "Add your trip dates first.", TripEnquiryErrorTypes.TripDatesRequired);

        var isTaxi = request.Kind == TripEnquiryKinds.Taxi;
        var day = request.Day;
        if (day > trip.DayCount)
            return BadRequestResponse($"This trip has no day {day}");
        var date = TripExecutionRules.NightDate(start, day);
        if (date < TripExecutionRules.TodayIst(DateTime.UtcNow))
            return ErrorResponse(422, "This day has already passed.", TripEnquiryErrorTypes.DayInPast);

        var planDay = ReadPlan(trip).Days?.FirstOrDefault(d => d.Day == day);
        var shortlist = (isTaxi
                ? planDay?.TaxiShortlist?.Select(x => x.TaxiId)
                : planDay?.StayShortlist?.Select(x => x.StayId))
            ?.Where(x => x != Guid.Empty).Distinct().ToList() ?? new List<Guid>();

        var history = await unitOfWork.TripEnquiries.GetByTripAsync(trip.Id);
        var targets = new List<Guid>();
        var preSkipped = new List<TripSkippedDto>();
        var askDays = new List<string>();
        var todayIst = TripExecutionRules.TodayIst(DateTime.UtcNow);
        foreach (var listingId in shortlist)
        {
            var latest = history.FirstOrDefault(x => x.Kind == request.Kind && x.ListingId == listingId && CoversDay(x, day, date));
            if (latest != null && TripEnquiryStatuses.IsOpen(latest.Status)) preSkipped.Add(Skip(listingId, TripSkipReasons.AlreadyOpen));
            else
            {
                targets.Add(listingId);
                if (isTaxi)
                {
                    var days = latest == null
                        ? new List<int> { day }
                        : CurrentTaxiDays(latest, trip.StartDate!.Value).Where(d => TripExecutionRules.NightDate(trip.StartDate!.Value, d) >= todayIst).ToList();
                    askDays.Add(string.Join(",", days.Count == 0 ? new List<int> { day } : days));
                }
            }
        }
        var sharedDays = isTaxi && askDays.Distinct().Count() == 1
            ? askDays[0].Split(',').Select(int.Parse).ToList()
            : new List<int> { day };
        if (targets.Count == 0)
            return ConflictResponse("Nothing to ask again. Shortlist a stay first.", TripEnquiryErrorTypes.AskAgainNothing);

        var create = new CreateTripEnquiryRequest
        {
            Kind = request.Kind,
            Day = isTaxi ? 0 : day,
            Days = isTaxi ? sharedDays : null,
            ListingIds = targets.Take(TripExecutionRules.MaxOpenPerNight).ToList(),
            AllNights = false,
        };
        return await CreateCoreAsync(trip, create, callerId, preSkipped, unitOfWork, db, conversations, chat, notifier, rateLimiter, cache, loggers, http);
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
        else if (ReadBooking(kept) != null)
        {
            if (await RewritePhoneCardAsync(db, kept, TripCardStatuses.Pending) is { } phoneRewrite) posts.Add(phoneRewrite);
            if (split.KeptAfter is { } phoneAfter && conversation != null && ReadBooking(phoneAfter) is { } afterBooking)
                posts.Add(await chat.PostAsync(conversation, callerId, TripMessageTypes.PhoneBooking,
                    PhoneCardPayload(phoneAfter, afterBooking, TripCardStatuses.Pending), "Booking recorded by phone"));
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
        needsEchoed = JsonSerializer.Deserialize<string[]>(shrunk.NeedsEchoedJson ?? "[]", TripJson.Options) ?? [],
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

    // Every day of the trip has a night. All nights = from this night through the last day's night (check-out the morning after the last day).
    private static (DateOnly CheckIn, DateOnly CheckOut) Nights(Trip trip, int day, bool allNights, int? nights = null)
    {
        var start = trip.StartDate!.Value;
        return (TripExecutionRules.NightDate(start, day), TripDateRules.StayCheckOut(start, trip.DayCount, day, allNights, nights));
    }

    private static bool Overlaps(TripEnquiry x, DateOnly checkIn, DateOnly checkOut)
        => x.CheckIn is { } a && x.CheckOut is { } b && TripExecutionRules.NightsOverlap(a, b, checkIn, checkOut);

    // At most MaxOpenPerNight open stay enquiries on every covered night, this listing's own rows not counted.
    private static bool FitsNightCap(List<TripEnquiry> tripOpen, Guid listingId, int alreadyAdded, DateOnly checkIn, DateOnly checkOut)
    {
        for (var night = checkIn; night < checkOut; night = night.AddDays(1))
        {
            var next = night.AddDays(1);
            var count = tripOpen.Count(x => x.ListingId != listingId && Overlaps(x, night, next)) + alreadyAdded;
            if (count >= TripExecutionRules.MaxOpenPerNight) return false;
        }
        return true;
    }

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

    // Lead context from the trip row: chosen days only; pickup from the request, else the trip settings.
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

        var time = !string.IsNullOrWhiteSpace(pickupTime) ? pickupTime.Trim() : settings.PickupTime;
        if (time != null && !System.Text.RegularExpressions.Regex.IsMatch(time, TripLeadRules.PickupTimePattern)) time = null;
        var place = TripTextSafety.CleanOrNull(
            !string.IsNullOrWhiteSpace(pickupPlace) ? pickupPlace : settings.PickupPlace, TripLeadRules.PickupPlaceMax);

        var context = new TripEnquiryContext
        {
            TripTitle = null,
            Adults = settings.Adults,
            Kids = settings.Kids,
            Rooms = null,
            Needs = (settings.Needs ?? []).Where(TripNeeds.All.Contains).Distinct().ToArray(),
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
        int? LocalDayRate, double? Latitude, double? Longitude);

    private static async Task<TaxiSnapshot?> LoadTaxiAsync(ApplicationDbContext db, Guid taxiId)
        => await db.TaxiListings.AsNoTracking()
            .Where(t => t.Id == taxiId)
            .Select(t => new TaxiSnapshot(
                t.Id, t.UserId, t.User.Name, t.VehicleType != null ? t.VehicleType.Name : null, t.Status, t.IsAvailable, t.IsDeleted,
                t.LocalDayRate,
                t.Latitude == null ? null : (double?)t.Latitude, t.Longitude == null ? null : (double?)t.Longitude))
            .FirstOrDefaultAsync();

    // The driver's name, as the taxi browse cards show it.
    private static string TaxiName(TaxiSnapshot taxi)
    {
        var name = TripTextSafety.Clean(taxi.DriverName, 120);
        if (name.Length > 0) return name;
        var vehicle = TripTextSafety.Clean(taxi.VehicleTypeName, 120);
        return vehicle.Length == 0 ? "Taxi" : vehicle;
    }

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
            Rooms = settings.Rooms,
            Needs = (settings.Needs ?? []).Where(TripNeeds.All.Contains).Distinct().ToArray(),
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
        Guid Id, Guid UserId, string? Name, string Status, bool AcceptingGuests, bool IsDeleted, double? Latitude, double? Longitude);

    private static async Task<StaySnapshot?> LoadStayAsync(ApplicationDbContext db, Guid stayId)
        => await db.StayListings.AsNoTracking()
            .Where(s => s.Id == stayId)
            .Select(s => new StaySnapshot(
                s.Id, s.UserId, s.Name, s.Status, s.AcceptingGuests, s.IsDeleted,
                s.Latitude == null ? null : (double?)s.Latitude, s.Longitude == null ? null : (double?)s.Longitude))
            .FirstOrDefaultAsync();

    private static string ListingName(StaySnapshot stay)
    {
        var name = TripTextSafety.Clean(stay.Name, 120);
        return name.Length == 0 ? "Stay" : name;
    }

    private static async Task<bool> IsBlockedAsync(IUnitOfWork unitOfWork, Guid a, Guid b)
        => await unitOfWork.UserBlocks.ExistsAsync(a, b) || await unitOfWork.UserBlocks.ExistsAsync(b, a);

    private static TripSkippedDto Skip(Guid listingId, string reason) => new() { ListingId = listingId, Reason = reason };

    // One count per request on both keys; asksHeader adds X-Trip-Asks-Left (hourly asks left) to the answer, 2xx or 429.
    private static async Task<IResult?> EnquiryRateLimitedAsync(IRateLimitService rateLimiter, HttpContext http, Guid userId, bool asksHeader = false)
    {
        var hourly = await rateLimiter.CheckAsync($"trip:enquiry:h:{userId}", TripExecutionRules.EnquiriesPerHour, Hour);
        if (asksHeader) http.Response.Headers[TripExecutionRules.AsksLeftHeader] = Math.Max(0, hourly.RemainingAttempts).ToString();
        if (!hourly.IsAllowed) return EnquiryLimited(http, hourly.RetryAfter ?? Hour);

        var daily = await rateLimiter.CheckAsync($"trip:enquiry:d:{userId}", TripExecutionRules.EnquiriesPerDay, Day24);
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

    private sealed record PhoneFields(
        string HostName, string HostPhone, string RoomName, int AgreedTotal, bool AdvancePaid, int? AdvanceAmount, string? Note);

    private static PhoneFields Fields(IPhoneBookingFields r) => new(
        TripTextSafety.Clean(r.HostName, TripExecutionRules.HostNameMax),
        r.HostPhone.Trim(),
        TripTextSafety.Clean(r.RoomName, TripExecutionRules.RoomNameMax),
        r.AgreedTotal,
        r.AdvancePaid,
        r.AdvancePaid ? r.AdvanceAmount : null,
        TripTextSafety.CleanOrNull(r.Note, TripExecutionRules.NoteMax));

    private static void ApplyPhoneFields(TripEnquiry e, PhoneFields f, DateOnly checkIn, DateOnly checkOut, DateTime now, bool keepRecordedAt)
    {
        var previous = keepRecordedAt ? ReadBooking(e) : null;
        e.CheckIn = checkIn;
        e.CheckOut = checkOut;
        e.AgreedTotal = f.AgreedTotal;
        e.UpdatedAt = now;
        e.PhoneBookingJson = JsonSerializer.Serialize(new TripPhoneBooking
        {
            HostName = f.HostName,
            HostPhone = f.HostPhone,
            RoomName = f.RoomName,
            Nights = checkOut.DayNumber - checkIn.DayNumber,
            AgreedTotal = f.AgreedTotal,
            AdvancePaid = f.AdvancePaid,
            AdvanceAmount = f.AdvanceAmount,
            Note = f.Note,
            RecordedAt = previous?.RecordedAt ?? now,
            UpdatedAt = previous != null ? now : null,
        }, TripJson.Options);
    }

    private static TripPhoneBooking? ReadBooking(TripEnquiry e) => TripEnquiryDtoBuilder.ReadPhoneBooking(e.PhoneBookingJson);

    private static string ReadTravellerName(TripEnquiry e)
        => TripEnquiryDtoBuilder.ReadContext(e.ContextJson).TravellerName ?? string.Empty;

    internal static object PhoneCardPayload(TripEnquiry e, TripPhoneBooking b, string status) => new
    {
        enquiryId = e.Id,
        hostName = b.HostName,
        roomName = b.RoomName,
        checkIn = IsoDate(e.CheckIn),
        checkOut = IsoDate(e.CheckOut),
        nights = b.Nights,
        agreedTotal = b.AgreedTotal,
        advancePaid = b.AdvancePaid,
        advanceAmount = b.AdvanceAmount,
        note = b.Note,
        status,
    };

    private static object OfferResponsePayload(TripEnquiry e, TripOffer offer, string answer) => new
    {
        enquiryId = e.Id,
        offerId = offer.Id,
        answer,
        total = offer.Total,
        checkIn = IsoDate(offer.CheckIn ?? e.CheckIn),
        checkOut = IsoDate(offer.CheckOut ?? e.CheckOut),
    };

    // The phone-booking card has no message id column: the latest trip_phone_booking message of the conversation for this enquiry.
    internal static async Task<TripChatPost?> RewritePhoneCardAsync(ApplicationDbContext db, TripEnquiry e, string status)
    {
        if (e.ConversationId is not { } conversationId) return null;
        var booking = ReadBooking(e);
        if (booking == null) return null;
        var candidates = await db.Messages
            .Where(m => m.ConversationId == conversationId && m.Type == TripMessageTypes.PhoneBooking)
            .OrderByDescending(m => m.CreatedAt)
            .Take(20)
            .ToListAsync();
        var message = candidates.FirstOrDefault(m => PayloadEnquiryId(m.PayloadJson) == e.Id);
        if (message == null) return null;
        var conversation = await db.Conversations.FirstOrDefaultAsync(c => c.Id == conversationId);
        if (conversation == null) return null;
        message.PayloadJson = JsonSerializer.Serialize(PhoneCardPayload(e, booking, status));
        var recipientUnread = conversation.RenterId == message.SenderId ? conversation.UnreadCountForOwner : conversation.UnreadCountForRenter;
        return new TripChatPost(conversation, message, recipientUnread, false);
    }

    private static Guid? PayloadEnquiryId(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("enquiryId", out var p) && p.TryGetGuid(out var g) ? g : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<TripPhoneBookingResultDto> PhoneResultAsync(
        IUnitOfWork unitOfWork, TripEnquiry e, string? token, Guid viewerId, DateTime now, TripExecutionOptions options, string travellerName)
    {
        var dto = await TripEnquiryDtoBuilder.BuildOneAsync(unitOfWork, e, viewerId, now);
        if (token == null) return new TripPhoneBookingResultDto { Enquiry = dto };

        var link = $"{options.PublicBaseUrl.TrimEnd('/')}/go/confirm/{token}";
        var booking = ReadBooking(e);
        var text = $"Namaste {booking?.HostName} ji. {TripDateText.FirstName(travellerName)} recorded a stay booking with you on Bakhli: "
            + $"{e.ListingName}, {TripDateText.Range(e.CheckIn, e.CheckOut)}, Rs {e.AgreedTotal ?? 0}. Is this correct? Tap: {link}";
        return new TripPhoneBookingResultDto { Enquiry = dto, ConfirmLink = link, WhatsAppText = text };
    }
}
