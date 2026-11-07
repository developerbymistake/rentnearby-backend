using System.Security.Claims;
using System.Text.Json;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using RentNearBy.Api.Extensions;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

// Host-side enquiry endpoints: list, send offer, not available, confirm / not me.
public static class TripOwnerHandlers
{
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 50;
    private static readonly TimeSpan OwnerActionWindow = TimeSpan.FromHours(1);
    private static readonly JsonSerializerOptions CardOptions = new(JsonSerializerDefaults.Web);

    // GET /trips/enquiries/owner (policy CanListStays on the route)
    public static async Task<IResult> GetMine(
        string? status, int? page, int? pageSize, ClaimsPrincipal principal, IUnitOfWork unitOfWork, ApplicationDbContext db)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var callerId)) return UnauthorizedResponse();

        var bucket = string.IsNullOrWhiteSpace(status) ? TripOwnerBuckets.New : status.Trim().ToLowerInvariant();
        if (!TripOwnerBuckets.All.Contains(bucket)) return BadRequestResponse("Status must be new, offered, confirmed or closed");
        var pageNo = Math.Max(1, page ?? 1);
        var size = Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize);
        var today = TripExecutionRules.TodayIst(DateTime.UtcNow);

        var (items, hasMore) = await OwnerPageAsync(db, callerId, bucket, today, pageNo, size);
        var counts = await OwnerCountsAsync(db, callerId, today);
        var built = await TripEnquiryViews.BuildAsync(unitOfWork, items, callerId);

        return OkResponse(new TripOwnerEnquiryPageDto
        {
            Items = items.Where(i => built.ContainsKey(i.Id)).Select(i => built[i.Id]).ToList(),
            HasMore = hasMore,
            Counts = new TripOwnerCountsDto { New = counts.New, Offered = counts.Offered, Confirmed = counts.Confirmed, Closed = counts.Closed },
        });
    }

    // A booked row (waiting for the host or confirmed) whose check-out has passed lists under closed; stay rows only.
    private static async Task<(IReadOnlyList<TripEnquiry> Items, bool HasMore)> OwnerPageAsync(
        ApplicationDbContext db, Guid ownerId, string bucket, DateOnly today, int page, int pageSize)
    {
        var newStatuses = TripOwnerBuckets.NewStatuses;
        var offeredStatuses = TripOwnerBuckets.OfferedStatuses;
        var confirmedStatuses = TripOwnerBuckets.ConfirmedStatuses;
        var endingStatuses = TripOwnerBuckets.EndingStatuses;
        var query = db.TripEnquiries.AsNoTracking().Where(e => e.OwnerUserId == ownerId && e.Kind == TripEnquiryKinds.Stay);
        var live = query.Where(e => !endingStatuses.Contains(e.Status) || e.CheckOut == null || e.CheckOut >= today);
        query = bucket switch
        {
            TripOwnerBuckets.New => live.Where(e => newStatuses.Contains(e.Status)),
            TripOwnerBuckets.Offered => live.Where(e => offeredStatuses.Contains(e.Status)),
            TripOwnerBuckets.Confirmed => live.Where(e => confirmedStatuses.Contains(e.Status)),
            _ => query.Where(e =>
                (!newStatuses.Contains(e.Status) && !offeredStatuses.Contains(e.Status) && !confirmedStatuses.Contains(e.Status))
                || (endingStatuses.Contains(e.Status) && e.CheckOut != null && e.CheckOut < today)),
        };

        var rows = await query
            .OrderByDescending(e => e.CreatedAt)
            .ThenByDescending(e => e.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize + 1)
            .ToListAsync();
        var hasMore = rows.Count > pageSize;
        if (hasMore) rows.RemoveAt(pageSize);
        return (rows, hasMore);
    }

    private static async Task<TripOwnerCounts> OwnerCountsAsync(ApplicationDbContext db, Guid ownerId, DateOnly today)
    {
        var groups = await db.TripEnquiries.AsNoTracking()
            .Where(e => e.OwnerUserId == ownerId && e.Kind == TripEnquiryKinds.Stay)
            .GroupBy(e => new { e.Status, Ended = e.CheckOut != null && e.CheckOut < today })
            .Select(g => new { g.Key.Status, g.Key.Ended, Count = g.Count() })
            .ToListAsync();
        int Sum(string bucket) => groups.Where(g => TripOwnerBuckets.Of(g.Status, g.Ended) == bucket).Sum(g => g.Count);
        return new TripOwnerCounts(
            Sum(TripOwnerBuckets.New), Sum(TripOwnerBuckets.Offered), Sum(TripOwnerBuckets.Confirmed), Sum(TripOwnerBuckets.Closed));
    }

    // POST /trips/enquiries/{id}/offers
    public static async Task<IResult> SendOffer(
        Guid id, SendTripOfferRequest request, IValidator<SendTripOfferRequest> validator,
        ClaimsPrincipal principal, IUnitOfWork unitOfWork, ApplicationDbContext db, IRateLimitService rateLimiter,
        ITripChatService chat, ITripNotifier notifier, HttpContext httpContext)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var (error, e, callerId) = await LoadForOwnerAsync(id, principal, unitOfWork, db, rateLimiter, httpContext);
        if (error != null) return error;
        e = e!;

        if (!TripEnquiryStatuses.IsUnconfirmed(e.Status)) return Closed();
        if (e.Kind != TripEnquiryKinds.Stay || e.TripId is not { } tripId) return Closed();

        var now = DateTime.UtcNow;
        var trip = await db.Trips.AsNoTracking().Where(t => t.Id == tripId)
            .Select(t => new { t.StartDate, t.EndDate }).FirstOrDefaultAsync();
        if (trip?.StartDate is not { } start || trip.EndDate is not { } end) return Closed();

        if (!TripDateRules.InsideTrip(start, end, request.CheckIn, request.CheckOut))
            return ErrorResponse(422, TripDateRules.OutsideTripMessage, TripEnquiryErrorTypes.OfferOutsideTrip);
        if (request.CheckIn < TripExecutionRules.TodayIst(now))
            return ErrorResponse(422, "That date has passed.", TripEnquiryErrorTypes.DayInPast);

        var offers = await unitOfWork.TripOffers.GetByEnquiryAsync(e.Id);
        if (offers.Count(o => o.Kind == TripOfferKinds.Offer) >= TripExecutionRules.MaxOffersPerEnquiry)
            return ErrorResponse(409, "Offer limit reached for this enquiry.", TripEnquiryErrorTypes.OfferLimit);

        TripOffer? counter = null;
        if (request.CounterOfferId is { } counterId)
        {
            counter = offers.FirstOrDefault(o => o.Id == counterId);
            if (counter == null || counter.Kind != TripOfferKinds.Counter || counter.Status != TripOfferStatuses.Pending)
                return ErrorResponse(409, "This price is no longer open.", TripEnquiryErrorTypes.OfferNotPending);
        }

        var lines = request.Lines.Select(l => new TripOfferLine
        {
            RoomName = TripTextSafety.CleanOrNull(l.RoomName, TripExecutionRules.RoomNameMax),
            Units = l.Units,
            Guests = l.Guests,
            PricePerNight = l.PricePerNight,
        }).ToList();
        var nights = request.CheckOut.DayNumber - request.CheckIn.DayNumber;
        var total = TripExecutionRules.ComputeTotal(lines, nights);
        if (total < TripExecutionRules.MinAmount || total > TripExecutionRules.MaxAmount)
            return BadRequestResponse(TripExecutionRules.TotalRangeMessage);
        if (request.AdvanceAmount is { } adv && (adv < 0 || adv > total))
            return BadRequestResponse("Advance must not be more than the total");

        var needs = (request.NeedsEchoed ?? []).Where(n => TripNeeds.All.Contains(n)).Distinct().ToArray();
        var datesDiffer = request.CheckIn != e.CheckIn || request.CheckOut != e.CheckOut;
        var validUntil = TripExecutionRules.OfferValidUntil(start, request.CheckIn, now);
        var firstAnswer = !offers.Any(o => o.Kind == TripOfferKinds.Offer) && now - e.CreatedAt <= TimeSpan.FromHours(24);

        var listed = await ListedPerNightAsync(db, e.ListingId, lines);
        var offer = new TripOffer
        {
            Id = Guid.NewGuid(),
            TripEnquiryId = e.Id,
            Kind = TripOfferKinds.Offer,
            SenderUserId = callerId,
            CheckIn = request.CheckIn,
            CheckOut = request.CheckOut,
            LinesJson = JsonSerializer.Serialize(lines, TripJson.Options),
            Nights = (short)nights,
            Total = total,
            Inclusions = TripTextSafety.CleanOrNull(request.Inclusions, 200),
            NeedsEchoedJson = JsonSerializer.Serialize(needs, TripJson.Options),
            AdvanceAmount = request.AdvanceAmount,
            BalanceNote = TripTextSafety.CleanOrNull(request.BalanceNote, TripExecutionRules.BalanceNoteMax),
            CancellationText = TripTextSafety.CleanOrNull(request.CancellationText, TripExecutionRules.CancellationMax),
            GstInvoice = request.GstInvoice,
            ValidUntil = validUntil,
            Status = TripOfferStatuses.Pending,
            CreatedAt = now,
        };

        var posts = new List<TripChatPost>();
        await unitOfWork.BeginTransactionAsync();
        try
        {
            foreach (var old in offers.Where(o => o.Status == TripOfferStatuses.Pending))
            {
                var acceptsCounter = counter != null && old.Id == counter.Id;
                old.Status = acceptsCounter ? TripOfferStatuses.Accepted : TripOfferStatuses.Superseded;
                if (acceptsCounter) old.RespondedAt = now;
                var card = acceptsCounter ? TripCardStatuses.Accepted : TripCardStatuses.Superseded;
                if (old.MessageId is { } oldMessage && await chat.RewriteStatusAsync(oldMessage, card) is { } rewrite)
                    posts.Add(rewrite);
            }

            var conversation = e.ConversationId is { } cid ? await unitOfWork.Conversations.GetByIdAsync(cid) : null;
            if (conversation != null)
            {
                var payload = new
                {
                    enquiryId = e.Id,
                    offerId = offer.Id,
                    checkIn = offer.CheckIn,
                    checkOut = offer.CheckOut,
                    nights,
                    lines,
                    total,
                    listedPerNight = listed,
                    inclusions = offer.Inclusions,
                    needsEchoed = needs,
                    advanceAmount = offer.AdvanceAmount,
                    balanceNote = offer.BalanceNote,
                    cancellationText = offer.CancellationText,
                    gstInvoice = offer.GstInvoice,
                    validUntil,
                    status = "pending",
                    datesDiffer,
                };
                var post = await chat.PostAsync(conversation, callerId, "trip_offer", payload, $"Offer · Rs {total}");
                offer.MessageId = post.Message.Id;
                posts.Add(post);
            }

            await unitOfWork.TripOffers.AddAsync(offer);
            e.Status = TripEnquiryStatuses.Offered;
            if (validUntil > e.ExpiresAt) e.ExpiresAt = validUntil;
            e.UpdatedAt = now;
            await unitOfWork.SaveChangesAsync();

            if (firstAnswer)
                await db.StayListings.Where(s => s.Id == e.ListingId)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.TripEnquiriesAnsweredIn24h, x => x.TripEnquiriesAnsweredIn24h + 1));

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

        await chat.AfterCommitAsync(posts);
        await TripPushSender.SendAsync(
            [new TripPendingPush(e, e.TravellerUserId,
                (row, ctx) => TripNotifications.TripOfferReceived(row, ctx with { CheckIn = offer.CheckIn, CheckOut = offer.CheckOut }),
                Total: total)],
            unitOfWork.TripEnquiries, notifier, httpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("TripOwner"));

        var dto = await TripEnquiryViews.BuildOneAsync(unitOfWork, e, callerId);
        return CreatedResponse(
            new TripOfferResultDto { Enquiry = dto, Offer = dto.OpenOffer ?? new TripOfferDto { Id = offer.Id } },
            $"/api/v1/trips/enquiries/{e.Id}");
    }

    // POST /trips/enquiries/{id}/not-available
    public static async Task<IResult> NotAvailable(
        Guid id, ClaimsPrincipal principal, IUnitOfWork unitOfWork, ApplicationDbContext db, IRateLimitService rateLimiter,
        ITripChatService chat, ITripNotifier notifier, HttpContext httpContext)
    {
        var (error, e, callerId) = await LoadForOwnerAsync(id, principal, unitOfWork, db, rateLimiter, httpContext);
        if (error != null) return error;
        e = e!;

        if (e.Status == TripEnquiryStatuses.NotAvailable) return OkResponse(await TripEnquiryViews.BuildOneAsync(unitOfWork, e, callerId));
        // Taxi leads answer through /trips/leads/{id}/not-available (answered counter, FareSent allowed).
        if (e.Kind != TripEnquiryKinds.Stay) return Closed();
        if (!TripEnquiryStatuses.IsUnconfirmed(e.Status)) return Closed();

        var now = DateTime.UtcNow;
        var posts = new List<TripChatPost>();
        await unitOfWork.BeginTransactionAsync();
        try
        {
            foreach (var open in await unitOfWork.TripOffers.GetByEnquiryAsync(e.Id))
            {
                if (open.Status != TripOfferStatuses.Pending) continue;
                open.Status = TripOfferStatuses.Superseded;
                if (open.MessageId is { } messageId && await chat.RewriteStatusAsync(messageId, "superseded") is { } rewrite)
                    posts.Add(rewrite);
            }

            e.Status = TripEnquiryStatuses.NotAvailable;
            e.FailReason = null;
            e.UpdatedAt = now;

            var conversation = e.ConversationId is { } cid ? await unitOfWork.Conversations.GetByIdAsync(cid) : null;
            if (conversation != null)
                posts.Add(await chat.PostSystemAsync(conversation, callerId, "notAvailable", "This stay is no longer available."));

            await unitOfWork.SaveChangesAsync();
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

        await chat.AfterCommitAsync(posts);
        await TripPushSender.SendAsync(
            [new TripPendingPush(e, e.TravellerUserId, TripNotifications.TripNotAvailable)],
            unitOfWork.TripEnquiries, notifier, httpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("TripOwner"));

        return OkResponse(await TripEnquiryViews.BuildOneAsync(unitOfWork, e, callerId));
    }

    // POST /trips/enquiries/{id}/host-confirm { confirm }
    public static async Task<IResult> HostConfirm(
        Guid id, HostConfirmRequest request, ClaimsPrincipal principal, IUnitOfWork unitOfWork, ApplicationDbContext db,
        IRateLimitService rateLimiter, ITripChatService chat, ITripNotifier notifier, HttpContext httpContext)
    {
        var (error, e, callerId) = await LoadForOwnerAsync(id, principal, unitOfWork, db, rateLimiter, httpContext);
        if (error != null) return error;
        e = e!;

        var now = DateTime.UtcNow;
        var posts = new List<TripChatPost>();
        HostAnswerOutcome outcome;
        await unitOfWork.BeginTransactionAsync();
        try
        {
            outcome = await TripHostAnswers.ApplyAsync(unitOfWork, chat, e, request.Confirm, TripHostConfirmVia.App, callerId, now, posts);
            if (outcome == HostAnswerOutcome.Applied) await unitOfWork.SaveChangesAsync();
            await unitOfWork.CommitTransactionAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            await unitOfWork.RollbackTransactionAsync();
            return Concurrent();
        }
        catch (DbUpdateException ex) when (TripEnquiryHandlers.IsOpenUniqueViolation(ex))
        {
            await unitOfWork.RollbackTransactionAsync();
            return Concurrent();
        }
        catch
        {
            await unitOfWork.RollbackTransactionAsync();
            throw;
        }

        switch (outcome)
        {
            case HostAnswerOutcome.NightTaken:
                return ErrorResponse(409, "The guest already booked another stay for these nights.", TripEnquiryErrorTypes.TripEnquiryClosed);
            case HostAnswerOutcome.NotAllowed:
                return Closed();
            case HostAnswerOutcome.Applied:
                await chat.AfterCommitAsync(posts);
                await TripHostAnswers.SendPushesAsync(unitOfWork, notifier, e, request.Confirm, TripHostConfirmVia.App,
                    httpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("TripOwner"));
                break;
        }

        return OkResponse(await TripEnquiryViews.BuildOneAsync(unitOfWork, e, callerId));
    }

    // POST /trips/enquiries/{id}/revised-offer. Price lock: the enquiry's AgreedTotal, AcceptedOfferId and dates stay
    // as they are until the guest accepts.
    public static async Task<IResult> RevisedOffer(
        Guid id, RevisedOfferRequest request, IValidator<RevisedOfferRequest> validator,
        ClaimsPrincipal principal, IUnitOfWork unitOfWork, ApplicationDbContext db, IRateLimitService rateLimiter,
        ITripChatService chat, ITripNotifier notifier, HttpContext httpContext)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var (error, e, callerId) = await LoadForOwnerAsync(id, principal, unitOfWork, db, rateLimiter, httpContext);
        if (error != null) return error;
        e = e!;

        var now = DateTime.UtcNow;
        var today = TripExecutionRules.TodayIst(now);
        if (e.Kind != TripEnquiryKinds.Stay || e.Status != TripEnquiryStatuses.ConfirmedByBoth || e.AcceptedOfferId == null
            || e.TripId is not { } tripId || e.ConversationId is not { } conversationId
            || e.CheckIn is not { } bookedIn || bookedIn < today)
            return RevisedNotAllowed();

        var trip = await db.Trips.AsNoTracking().Where(t => t.Id == tripId)
            .Select(t => new { t.StartDate, t.EndDate }).FirstOrDefaultAsync();
        if (trip?.StartDate is not { } start || trip.EndDate is not { } end) return RevisedNotAllowed();
        if (!TripDateRules.InsideTrip(start, end, request.CheckIn, request.CheckOut))
            return ErrorResponse(422, TripDateRules.OutsideTripMessage, TripEnquiryErrorTypes.OfferOutsideTrip);
        if (request.CheckIn < today)
            return ErrorResponse(422, "That date has passed.", TripEnquiryErrorTypes.DayInPast);
        if (await TripEnquiryHandlers.FailedNightsBlockAsync(db, e, request.CheckIn, request.CheckOut) is { } failedNights)
            return failedNights;

        var offers = await unitOfWork.TripOffers.GetByEnquiryAsync(e.Id);
        if (offers.Count(o => o.Kind == TripOfferKinds.Revised) >= TripOfferRules.MaxRevised)
            return ErrorResponse(409, "Revised offer limit reached.", TripEnquiryErrorTypes.OfferLimit);

        TripOffer? change = null;
        if (request.ChangeRequestId is { } changeId)
        {
            change = offers.FirstOrDefault(o => o.Id == changeId);
            if (change == null || change.Kind != TripOfferKinds.ChangeRequest || change.Status != TripOfferStatuses.Pending)
                return ChangeNotPending();
        }

        var lines = CleanLines(request.Lines);
        var nights = request.CheckOut.DayNumber - request.CheckIn.DayNumber;
        var total = TripExecutionRules.ComputeTotal(lines, nights);
        if (total < TripExecutionRules.MinAmount || total > TripExecutionRules.MaxAmount)
            return BadRequestResponse(TripExecutionRules.TotalRangeMessage);
        if (request.AdvanceAmount is { } adv && (adv < 0 || adv > total))
            return BadRequestResponse("Advance must not be more than the total");

        var reason = TripTextSafety.Clean(request.Reason, TripOfferRules.ReasonMax);
        if (reason.Length == 0) return BadRequestResponse($"Reason must be 1 to {TripOfferRules.ReasonMax} characters");

        var validUntil = TripExecutionRules.OfferValidUntil(start, request.CheckIn, now);
        var datesDiffer = request.CheckIn != e.CheckIn || request.CheckOut != e.CheckOut;
        var offer = new TripOffer
        {
            Id = Guid.NewGuid(),
            TripEnquiryId = e.Id,
            Kind = TripOfferKinds.Revised,
            SenderUserId = callerId,
            CheckIn = request.CheckIn,
            CheckOut = request.CheckOut,
            LinesJson = JsonSerializer.Serialize(lines, TripJson.Options),
            Nights = (short)nights,
            Total = total,
            Inclusions = TripTextSafety.CleanOrNull(request.Inclusions, 200),
            NeedsEchoedJson = "[]",
            AdvanceAmount = request.AdvanceAmount,
            BalanceNote = TripTextSafety.CleanOrNull(request.BalanceNote, TripExecutionRules.BalanceNoteMax),
            CancellationText = TripTextSafety.CleanOrNull(request.CancellationText, TripExecutionRules.CancellationMax),
            GstInvoice = request.GstInvoice,
            Note = reason,
            ValidUntil = validUntil,
            Status = TripOfferStatuses.Pending,
            CreatedAt = now,
        };

        var posts = new List<TripChatPost>();
        await unitOfWork.BeginTransactionAsync();
        try
        {
            foreach (var old in offers.Where(o => o.Status == TripOfferStatuses.Pending
                && (o.Kind == TripOfferKinds.Revised || (change != null && o.Id == change.Id))))
            {
                old.Status = TripOfferStatuses.Superseded;
                old.RespondedAt ??= now;
                if (old.MessageId is { } oldMessage && await chat.RewriteStatusAsync(oldMessage, TripCardStatuses.Superseded) is { } rewrite)
                    posts.Add(rewrite);
            }

            var conversation = await unitOfWork.Conversations.GetByIdAsync(conversationId);
            if (conversation != null)
            {
                if (change != null)
                    posts.Add(await chat.PostSystemAsync(conversation, callerId, TripSystemLines.ChangeDifferentOffer,
                        TripSystemLines.Text(TripSystemLines.ChangeDifferentOffer)));

                var payload = new
                {
                    enquiryId = e.Id,
                    offerId = offer.Id,
                    changeRequestId = change?.Id,
                    oldTotal = e.AgreedTotal,
                    total,
                    nights,
                    checkIn = offer.CheckIn,
                    checkOut = offer.CheckOut,
                    lines,
                    reason,
                    advanceAmount = offer.AdvanceAmount,
                    balanceNote = offer.BalanceNote,
                    cancellationText = offer.CancellationText,
                    gstInvoice = offer.GstInvoice,
                    inclusions = offer.Inclusions,
                    validUntil,
                    datesDiffer,
                    advanceAgreed = e.AdvanceStatus != TripAdvanceStatuses.None ? e.AdvanceAmount : null,
                    status = TripCardStatuses.Pending,
                };
                var post = await chat.PostAsync(conversation, callerId, TripMessageTypes.RevisedOffer, payload, $"Revised offer · Rs {total}");
                offer.MessageId = post.Message.Id;
                posts.Add(post);
            }

            await unitOfWork.TripOffers.AddAsync(offer);
            e.UpdatedAt = now;
            await unitOfWork.SaveChangesAsync();
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

        await chat.AfterCommitAsync(posts);
        await TripPushSender.SendAsync(
            [new TripPendingPush(e, e.TravellerUserId,
                (row, ctx) => TripNotifications.TripRevisedOffer(row, ctx with { CheckIn = offer.CheckIn, CheckOut = offer.CheckOut }),
                Total: total)],
            unitOfWork.TripEnquiries, notifier, httpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("TripOwner"));

        var dto = await TripEnquiryViews.BuildOneAsync(unitOfWork, e, callerId);
        return CreatedResponse(
            new TripOfferResultDto { Enquiry = dto, Offer = dto.PendingRevised ?? new TripOfferDto { Id = offer.Id } },
            $"/api/v1/trips/enquiries/{e.Id}");
    }

    // POST /trips/enquiries/{id}/change-request/{offerId}/accept. Moves the snapshot to the guest's request; the host
    // cannot replace another booked stay of the guest (the guest cancels it first).
    public static async Task<IResult> AcceptChange(
        Guid id, Guid offerId, ClaimsPrincipal principal, IUnitOfWork unitOfWork, ApplicationDbContext db,
        IRateLimitService rateLimiter, ITripChatService chat, ITripNotifier notifier, HttpContext httpContext)
    {
        var (error, e, callerId) = await LoadForOwnerAsync(id, principal, unitOfWork, db, rateLimiter, httpContext);
        if (error != null) return error;
        e = e!;

        if (e.AcceptedOfferId == offerId) return OkResponse(await TripEnquiryViews.BuildOneAsync(unitOfWork, e, callerId));

        var now = DateTime.UtcNow;
        var offers = await unitOfWork.TripOffers.GetByEnquiryAsync(e.Id);
        var change = offers.FirstOrDefault(o => o.Id == offerId);
        if (change == null || change.Kind != TripOfferKinds.ChangeRequest || e.Kind != TripEnquiryKinds.Stay)
            return ChangeNotPending();
        if (change.Status == TripOfferStatuses.Expired
            || (change.Status == TripOfferStatuses.Pending && change.ValidUntil is { } valid && valid <= now))
            return ErrorResponse(409, "This change request has expired.", TripEnquiryErrorTypes.OfferExpired);
        if (change.Status != TripOfferStatuses.Pending || e.Status != TripEnquiryStatuses.ConfirmedByBoth
            || change.CheckIn is not { } checkIn || change.CheckOut is not { } checkOut || e.TripId is not { } tripId)
            return ChangeNotPending();

        var trip = await db.Trips.AsNoTracking().Where(t => t.Id == tripId)
            .Select(t => new { t.StartDate, t.EndDate }).FirstOrDefaultAsync();
        if (trip?.StartDate is not { } start || trip.EndDate is not { } end || !TripDateRules.InsideTrip(start, end, checkIn, checkOut))
            return ErrorResponse(422, TripDateRules.OutsideTripMessage, TripEnquiryErrorTypes.OfferOutsideTrip);
        var todayIst = TripExecutionRules.TodayIst(now);
        if ((checkIn != e.CheckIn && checkIn < todayIst) || checkOut <= todayIst)
            return ErrorResponse(422, "That date has passed.", TripEnquiryErrorTypes.DayInPast);
        if (await TripEnquiryHandlers.FailedNightsBlockAsync(db, e, checkIn, checkOut) is { } failedNights)
            return failedNights;

        var blocking =(await unitOfWork.TripEnquiries.GetOpenByTripAsync(tripId, TripEnquiryKinds.Stay))
            .FirstOrDefault(o => o.Id != e.Id && TripEnquiryStatuses.IsBooked(o.Status)
                && o.CheckIn is { } a && o.CheckOut is { } b && TripExecutionRules.NightsOverlap(a, b, checkIn, checkOut));
        if (blocking != null)
            return ErrorResponse(409, "The guest already has another stay on those nights.", TripEnquiryErrorTypes.NightAlreadyConfirmed,
                new TripNightConflictDto { EnquiryId = blocking.Id, ListingName = blocking.ListingName });

        var lines = TripEnquiryDtoBuilder.ReadLines(change.LinesJson);
        var guestId = e.TravellerUserId ?? callerId;
        var plan = await TripEnquiryHandlers.LoadPlanAsync(db, tripId);
        var posts = new List<TripChatPost>();
        var pushes = new List<TripPendingPush>();
        await unitOfWork.BeginTransactionAsync();
        try
        {
            // One stay per night, as on accept: other hosts' asks on the new nights close, a racing booking answers 409.
            if (await TripEnquiryHandlers.ApplyNightRulesAsync(db, chat, e, checkIn, checkOut, closeOthers: true, replaceId: null,
                    guestId, posts, pushes, now) is { } conflict)
            {
                await unitOfWork.RollbackTransactionAsync();
                return conflict;
            }

            foreach (var old in offers)
            {
                if (old.Id == change.Id) continue;
                var wasAccepted = old.Id == e.AcceptedOfferId;
                // A revised offer priced against the old snapshot cannot stay open once the snapshot moves.
                var staleRevised = old.Kind == TripOfferKinds.Revised && old.Status == TripOfferStatuses.Pending;
                if (!wasAccepted && !staleRevised) continue;
                old.Status = TripOfferStatuses.Superseded;
                if (!staleRevised) continue;
                old.RespondedAt ??= now;
                if (old.MessageId is { } staleMessage && await chat.RewriteStatusAsync(staleMessage, TripCardStatuses.Superseded) is { } staleRewrite)
                    posts.Add(staleRewrite);
            }

            change.Status = TripOfferStatuses.Accepted;
            change.RespondedAt = now;
            e.AcceptedOfferId = change.Id;
            e.AgreedTotal = change.Total;
            e.CheckIn = checkIn;
            e.CheckOut = checkOut;
            e.Day = TripExecutionRules.DayOf(start, checkIn);
            var context = TripEnquiryDtoBuilder.ReadContext(e.ContextJson);
            context.Adults = change.Adults ?? context.Adults;
            context.Kids = change.Kids ?? context.Kids;
            context.Rooms = lines.Count > 0 ? lines.Sum(l => l.Units) : context.Rooms;
            TripEnquiryHandlers.RebuildStayContext(e, context, plan, start, checkIn, checkOut);
            e.UpdatedAt = now;
            await unitOfWork.SaveChangesAsync();

            if (change.MessageId is { } messageId && await chat.RewriteStatusAsync(messageId, TripCardStatuses.Accepted) is { } rewrite)
                posts.Add(rewrite);
            if (e.ConversationId is { } conversationId && await unitOfWork.Conversations.GetByIdAsync(conversationId) is { } conversation)
                posts.Add(await chat.PostSystemAsync(conversation, callerId, TripSystemLines.ChangeAccepted,
                    TripSystemLines.Text(TripSystemLines.ChangeAccepted)));
            await unitOfWork.CommitTransactionAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            await unitOfWork.RollbackTransactionAsync();
            return Concurrent();
        }
        catch (DbUpdateException ex) when (TripEnquiryHandlers.IsOpenUniqueViolation(ex))
        {
            await unitOfWork.RollbackTransactionAsync();
            return Concurrent();
        }
        catch
        {
            await unitOfWork.RollbackTransactionAsync();
            throw;
        }

        await chat.AfterCommitAsync(posts);
        pushes.Add(new TripPendingPush(e, e.TravellerUserId, TripNotifications.TripChangeAccepted, Total: change.Total));
        await TripPushSender.SendAsync(
            pushes, unitOfWork.TripEnquiries, notifier, httpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("TripOwner"));

        return OkResponse(await TripEnquiryViews.BuildOneAsync(unitOfWork, e, callerId));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    // Caller, rate limit, enquiry (tracked), and the owner side check. One place so no host action skips a step.
    private static async Task<(IResult? Error, TripEnquiry? Enquiry, Guid CallerId)> LoadForOwnerAsync(
        Guid id, ClaimsPrincipal principal, IUnitOfWork unitOfWork, ApplicationDbContext db,
        IRateLimitService rateLimiter, HttpContext httpContext)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var callerId)) return (UnauthorizedResponse(), null, Guid.Empty);

        var rl = await rateLimiter.CheckAsync($"trip:owner:{callerId}", TripExecutionRules.OwnerActionsPerHour, OwnerActionWindow);
        if (!rl.IsAllowed)
        {
            if (rl.RetryAfter is { } retry) httpContext.Response.Headers["Retry-After"] = ((int)retry.TotalSeconds).ToString();
            return (TooManyRequestsResponse(), null, callerId);
        }

        var e = await unitOfWork.TripEnquiries.GetByIdTrackedAsync(id);
        if (e == null) return (ErrorResponse(404, "Enquiry not found", TripEnquiryErrorTypes.TripEnquiryNotFound), null, callerId);

        var denied = await TripEnquiryAuth.RequireOwnerAsync(e, callerId, db);
        return denied != null ? (denied, null, callerId) : (null, e, callerId);
    }

    private static IResult Closed() =>
        ErrorResponse(409, "This enquiry is closed.", TripEnquiryErrorTypes.TripEnquiryClosed);

    private static IResult Concurrent() =>
        ErrorResponse(409, "This enquiry just changed.", TripEnquiryErrorTypes.ConcurrentUpdate);

    private static IResult RevisedNotAllowed() =>
        ErrorResponse(409, "A revised offer needs a booking confirmed by both.", TripEnquiryErrorTypes.RevisedNotAllowed);

    private static IResult ChangeNotPending() =>
        ErrorResponse(409, "This change request is no longer open.", TripEnquiryErrorTypes.ChangeNotPending);

    private static List<TripOfferLine> CleanLines(IEnumerable<TripOfferLine> lines) => lines.Select(l => new TripOfferLine
    {
        RoomName = TripTextSafety.CleanOrNull(l.RoomName, TripExecutionRules.RoomNameMax),
        Units = l.Units,
        Guests = l.Guests,
        PricePerNight = l.PricePerNight,
    }).ToList();

    // The stay's own price of the first line's room, shown next to the host's offer.
    private static async Task<int?> ListedPerNightAsync(ApplicationDbContext db, Guid stayId, IReadOnlyList<TripOfferLine> lines)
    {
        var name = lines.FirstOrDefault()?.RoomName;
        if (string.IsNullOrEmpty(name)) return null;
        var price = await db.StayRooms.AsNoTracking()
            .Where(r => r.StayListingId == stayId && r.RoomName == name)
            .Select(r => (int?)r.PricePerNight).FirstOrDefaultAsync();
        return price;
    }
}

// Thin delegate so every list and detail path builds DTOs in one place (TripEnquiryDtoBuilder).
internal static class TripEnquiryViews
{
    public static async Task<Dictionary<Guid, TripEnquiryDto>> BuildAsync(
        IUnitOfWork unitOfWork, IReadOnlyCollection<TripEnquiry> items, Guid viewerId)
    {
        var built = await TripEnquiryDtoBuilder.BuildManyAsync(unitOfWork, items.ToList(), viewerId, DateTime.UtcNow);
        return built.ToDictionary(d => d.Id);
    }

    public static Task<TripEnquiryDto> BuildOneAsync(IUnitOfWork unitOfWork, TripEnquiry e, Guid viewerId) =>
        TripEnquiryDtoBuilder.BuildOneAsync(unitOfWork, e, viewerId, DateTime.UtcNow);
}

internal enum HostAnswerOutcome { Applied, AlreadyDone, NotAllowed, NightTaken }

// Host answer to a phone booking, shared by the app (TripOwnerHandlers.HostConfirm) and the public link page.
internal static class TripHostAnswers
{
    // Mutates the tracked enquiry and stages the card/system rows; the caller saves and commits.
    public static async Task<HostAnswerOutcome> ApplyAsync(
        IUnitOfWork unitOfWork, ITripChatService chat, TripEnquiry e, bool confirm, string via, Guid? byUserId,
        DateTime now, List<TripChatPost> posts)
    {
        var overrideOpen = via == TripHostConfirmVia.App
            && e.HostConfirmAnsweredVia == TripHostConfirmVia.Link
            && e.HostConfirmAnsweredAt is { } answeredAt
            && now <= answeredAt.AddDays(TripExecutionRules.HostLinkOverrideDays);

        if (confirm)
        {
            if (e.Status == TripEnquiryStatuses.ConfirmedByBoth && e.HostConfirmAnswer == TripHostConfirmAnswers.Yes)
                return HostAnswerOutcome.AlreadyDone;

            var fresh = e.Status == TripEnquiryStatuses.ConfirmedByTraveller && e.HostConfirmAnsweredAt == null;
            var overridden = e.Status == TripEnquiryStatuses.Failed && e.FailReason == TripFailReasons.HostDenied && overrideOpen;
            if (!fresh && !overridden) return HostAnswerOutcome.NotAllowed;

            if (overridden && await NightTakenAsync(unitOfWork, e)) return HostAnswerOutcome.NightTaken;

            e.Status = TripEnquiryStatuses.ConfirmedByBoth;
            e.FailReason = null;
            e.ConfirmedByOwnerAt = now;
        }
        else
        {
            if (e.Status == TripEnquiryStatuses.Failed && e.FailReason == TripFailReasons.HostDenied
                && e.HostConfirmAnswer == TripHostConfirmAnswers.NotMe)
                return HostAnswerOutcome.AlreadyDone;

            var fresh = e.Status == TripEnquiryStatuses.ConfirmedByTraveller && e.HostConfirmAnsweredAt == null;
            var overridden = e.Status == TripEnquiryStatuses.ConfirmedByBoth && overrideOpen;
            if (!fresh && !overridden) return HostAnswerOutcome.NotAllowed;

            await TripEnquiryOps.CloseOpenAdvanceAsync(chat, e, posts);
            e.Status = TripEnquiryStatuses.Failed;
            e.FailReason = TripFailReasons.HostDenied;
            e.ConfirmedByOwnerAt = null;
        }

        e.HostConfirmAnswer = confirm ? TripHostConfirmAnswers.Yes : TripHostConfirmAnswers.NotMe;
        e.HostConfirmAnsweredAt = now;
        e.HostConfirmAnsweredVia = via;
        e.HostConfirmAnsweredByUserId = byUserId;
        e.UpdatedAt = now;

        if (e.ConversationId is { } conversationId && e.OwnerUserId is { } actor)
        {
            var conversation = await unitOfWork.Conversations.GetByIdAsync(conversationId);
            if (conversation != null)
            {
                if (await chat.FindCardMessageIdAsync(conversationId, "trip_phone_booking", e.Id) is { } cardId
                    && await chat.RewriteStatusAsync(cardId, confirm ? "confirmed" : "denied") is { } rewrite)
                    posts.Add(rewrite);

                posts.Add(confirm
                    ? await chat.PostSystemAsync(conversation, actor, "hostConfirmed", "Host confirmed the booking.")
                    : await chat.PostSystemAsync(conversation, actor, "hostDenied", "Host says this booking is not theirs."));
            }
        }

        return HostAnswerOutcome.Applied;
    }

    // Traveller push, plus the host push when the answer came from the link.
    public static async Task SendPushesAsync(
        IUnitOfWork unitOfWork, ITripNotifier notifier, TripEnquiry e, bool confirm, string via, ILogger? logger = null)
    {
        var pushes = new List<TripPendingPush>
        {
            new(e, e.TravellerUserId, confirm ? TripNotifications.TripHostConfirmed : TripNotifications.TripHostDenied),
        };
        if (via == TripHostConfirmVia.Link)
            pushes.Add(new(e, e.OwnerUserId, TripNotifications.TripHostLinkAnswered, Confirmed: confirm));
        await TripPushSender.SendAsync(pushes, unitOfWork.TripEnquiries, notifier, logger ?? NullLogger.Instance);
    }

    // Another confirmed stay of the same trip already holds these nights, or the guest has a newer open row with this
    // stay for them (the open unique index would refuse the restore).
    private static async Task<bool> NightTakenAsync(IUnitOfWork unitOfWork, TripEnquiry e)
    {
        if (e.TripId is not { } tripId || e.CheckIn is not { } checkIn || e.CheckOut is not { } checkOut) return false;
        var open = await unitOfWork.TripEnquiries.GetOpenByTripAsync(tripId, TripEnquiryKinds.Stay);
        return open.Any(o => o.Id != e.Id
            && ((o.ListingId == e.ListingId && o.Day == e.Day)
                || ((TripEnquiryStatuses.IsConfirmed(o.Status) || o.ListingId == e.ListingId)
                    && o.CheckIn is { } a && o.CheckOut is { } b
                    && TripExecutionRules.NightsOverlap(a, b, checkIn, checkOut))));
    }
}
