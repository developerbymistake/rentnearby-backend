using System.Security.Claims;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

// Stay advance (no payment in the app): the host asks, the guest pays him directly and says the proof is sent,
// the host marks it received. The UPI ID and WhatsApp number travel only inside the trip_advance_request chat card.
public static class TripAdvanceHandlers
{
    private static readonly TimeSpan OwnerActionWindow = TimeSpan.FromHours(1);

    private static IResult NotAllowed(string message = "Advance can be asked only on a booking confirmed by both.")
        => ConflictResponse(message, TripEnquiryErrorTypes.AdvanceNotAllowed);

    private static IResult Concurrent()
        => ConflictResponse("This enquiry just changed.", TripEnquiryErrorTypes.ConcurrentUpdate);

    // ---------- POST /trips/enquiries/{id}/advance/ask (host) ----------

    public static async Task<IResult> Ask(
        Guid id, AskAdvanceRequest request, IValidator<AskAdvanceRequest> validator, ClaimsPrincipal principal,
        IUnitOfWork unitOfWork, ApplicationDbContext db, IRateLimitService rateLimiter, ITripChatService chat,
        ITripNotifier notifier, ILoggerFactory loggers, HttpContext http)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var (error, e, callerId) = await LoadForOwnerAsync(id, principal, unitOfWork, db, rateLimiter, http);
        if (error != null) return error;
        e = e!;

        var now = DateTime.UtcNow;
        if (e.Kind != TripEnquiryKinds.Stay || e.Status != TripEnquiryStatuses.ConfirmedByBoth || e.ConversationId == null
            || e.AdvanceStatus is not (TripAdvanceStatuses.None or TripAdvanceStatuses.Pending or TripAdvanceStatuses.ProofSent)
            || e.CheckIn is not { } checkIn || checkIn < TripExecutionRules.TodayIst(now))
            return NotAllowed();

        if (await TripGuards.TripNotCompletedAsync(db, e.TripId) is { } completed) return completed;
        if (await TripGuards.ListingApprovedAsync(db, e.Kind, e.ListingId) is { } gone) return gone;

        var cap = TripAdvanceRules.HoldCap(checkIn, now);
        if (cap <= now.AddHours(TripAdvanceRules.HoldMinHours)) return NotAllowed("Too late to hold.");

        var acceptedAdvance = e.AcceptedOfferId is { } acceptedId
            ? (await unitOfWork.TripOffers.GetByEnquiryAsync(e.Id)).FirstOrDefault(o => o.Id == acceptedId)?.AdvanceAmount : null;
        var maxAmount = TripHostGuards.MaxAdvance(e.AgreedTotal, acceptedAdvance);
        if (request.Amount > maxAmount) return BadRequestResponse($"Advance must be at most Rs {maxAmount}");

        var holdUntil = request.HoldUntil.Kind == DateTimeKind.Utc ? request.HoldUntil : request.HoldUntil.ToUniversalTime();
        if (!TripAdvanceRules.HoldAllowed(holdUntil, checkIn, now))
            return ErrorResponse(422, $"Hold must end between 1 h from now and {TripDateText.DayTimeIst(cap, now)}", "BadRequest");

        var conversation = await db.Conversations.FirstOrDefaultAsync(c => c.Id == e.ConversationId);
        if (conversation == null) return NotAllowed();

        var upiId = request.UpiId.Trim();
        var whatsApp = request.WhatsAppNumber.Trim();
        var previous = e.AdvanceStatus;
        var posts = new List<TripChatPost>();
        await unitOfWork.BeginTransactionAsync();
        try
        {
            // One open advance per enquiry: a new ask replaces the old cards.
            if (previous is TripAdvanceStatuses.Pending or TripAdvanceStatuses.ProofSent)
                await RewriteCardAsync(chat, conversation.Id, TripMessageTypes.AdvanceRequest, e.Id, TripCardStatuses.Replaced, posts);
            if (previous == TripAdvanceStatuses.ProofSent)
                await RewriteCardAsync(chat, conversation.Id, TripMessageTypes.AdvanceProofSent, e.Id, TripCardStatuses.Replaced, posts);

            e.AdvanceStatus = TripAdvanceStatuses.Pending;
            e.AdvanceAmount = request.Amount;
            e.HoldUntil = holdUntil;
            e.AdvanceAskedAt = now;
            e.ProofSentAt = null;
            e.AdvanceReceivedAt = null;
            e.AdvanceNotYetAt = null;
            e.LastHostReminderAt = null;
            e.HostReminderCount = 0;
            e.UpdatedAt = now;
            await db.SaveChangesAsync();

            posts.Add(await chat.PostAsync(conversation, callerId, TripMessageTypes.AdvanceRequest, new
            {
                enquiryId = e.Id,
                amount = request.Amount,
                holdUntil,
                upiId,
                whatsApp,
                stayName = e.ListingName,
                checkIn = IsoDate(e.CheckIn),
                checkOut = IsoDate(e.CheckOut),
                agreedTotal = e.AgreedTotal,
                status = TripCardStatuses.Pending,
            }, $"Advance · Rs {request.Amount}"));

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

        await TripEnquiryHandlers.AfterCommitAsync(chat, posts,
            [new TripPendingPush(e, e.TravellerUserId, TripNotifications.TripAdvanceRequested)], unitOfWork, notifier, loggers);
        return OkResponse(await TripEnquiryDtoBuilder.BuildOneAsync(unitOfWork, e, callerId, now));
    }

    // ---------- POST /trips/enquiries/{id}/advance/proof-sent (guest) ----------

    public static async Task<IResult> ProofSent(
        Guid id, ClaimsPrincipal principal, IUnitOfWork unitOfWork, ApplicationDbContext db, ITripChatService chat,
        ITripNotifier notifier, ILoggerFactory loggers)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var callerId)) return UnauthorizedResponse();

        var e = await unitOfWork.TripEnquiries.GetByIdTrackedAsync(id);
        if (TripEnquiryAuth.RequireTraveller(e, callerId) is { } denied) return denied;
        e = e!;

        var now = DateTime.UtcNow;
        if (e.AdvanceStatus is TripAdvanceStatuses.ProofSent or TripAdvanceStatuses.Received)
            return OkResponse(await TripEnquiryDtoBuilder.BuildOneAsync(unitOfWork, e, callerId, now));
        if (e.AdvanceStatus == TripAdvanceStatuses.HoldExpired) return NotAllowed("The hold has ended.");
        if (await TripGuards.TripNotCompletedAsync(db, e.TripId) is { } completed) return completed;
        if (e.Status != TripEnquiryStatuses.ConfirmedByBoth || e.AdvanceStatus != TripAdvanceStatuses.Pending)
            return NotAllowed();
        if (e.HoldUntil is not { } hold || hold <= now)
            return ConflictResponse("The hold has ended.", TripEnquiryErrorTypes.HoldExpired);

        var conversation = e.ConversationId is { } cid ? await db.Conversations.FirstOrDefaultAsync(c => c.Id == cid) : null;
        var guestPhone = TenDigits(await db.Users.AsNoTracking()
            .Where(u => u.Id == callerId).Select(u => u.PhoneNumber).FirstOrDefaultAsync());

        var posts = new List<TripChatPost>();
        await unitOfWork.BeginTransactionAsync();
        try
        {
            e.AdvanceStatus = TripAdvanceStatuses.ProofSent;
            e.ProofSentAt = now;
            e.LastHostReminderAt = now;
            e.HostReminderCount = 1;
            e.UpdatedAt = now;
            await db.SaveChangesAsync();

            if (conversation != null)
            {
                await RewriteCardAsync(chat, conversation.Id, TripMessageTypes.AdvanceRequest, e.Id, TripCardStatuses.ProofSent, posts);
                posts.Add(await chat.PostAsync(conversation, callerId, TripMessageTypes.AdvanceProofSent, new
                {
                    enquiryId = e.Id,
                    amount = e.AdvanceAmount,
                    holdUntil = e.HoldUntil,
                    guestPhone,
                    proofSentAt = now,
                    status = TripCardStatuses.Pending,
                }, $"Proof sent for Rs {e.AdvanceAmount ?? 0}"));
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

        // Hour 0 at once; the timer sends hour 1, 2, ... until the host marks it or the hold ends.
        await TripEnquiryHandlers.AfterCommitAsync(chat, posts,
            [new TripPendingPush(e, e.OwnerUserId, TripNotifications.TripAdvanceProofSent, DedupeKey: TripAdvanceRules.ProofKey(e.Id, now, 0))],
            unitOfWork, notifier, loggers);
        return OkResponse(await TripEnquiryDtoBuilder.BuildOneAsync(unitOfWork, e, callerId, now));
    }

    // ---------- POST /trips/enquiries/{id}/advance/received (host; also the late mark after the hold ended) ----------

    public static async Task<IResult> Received(
        Guid id, ClaimsPrincipal principal, IUnitOfWork unitOfWork, ApplicationDbContext db, IRateLimitService rateLimiter,
        ITripChatService chat, ITripNotifier notifier, ILoggerFactory loggers, HttpContext http)
    {
        var (error, e, callerId) = await LoadForOwnerAsync(id, principal, unitOfWork, db, rateLimiter, http);
        if (error != null) return error;
        e = e!;

        var now = DateTime.UtcNow;
        if (e.AdvanceStatus == TripAdvanceStatuses.Received)
            return OkResponse(await TripEnquiryDtoBuilder.BuildOneAsync(unitOfWork, e, callerId, now));

        var onTime = e.Status == TripEnquiryStatuses.ConfirmedByBoth
            && e.AdvanceStatus is TripAdvanceStatuses.Pending or TripAdvanceStatuses.ProofSent;
        var late = e.Status == TripEnquiryStatuses.Failed && e.FailReason == TripFailReasons.HoldExpired
            && e.AdvanceStatus == TripAdvanceStatuses.HoldExpired
            && e.CheckIn is { } checkIn && checkIn >= TripExecutionRules.TodayIst(now)
            && e.TripId != null && e.Kind == TripEnquiryKinds.Stay;
        if (!onTime && !late) return NotAllowed();

        if (late && await MovedOnAsync(db, e)) return ConflictResponse(
            "The guest has booked another stay. Sort the advance with him directly.", TripEnquiryErrorTypes.TravellerMovedOn);

        var hadProof = e.ProofSentAt != null;
        var conversation = e.ConversationId is { } cid ? await db.Conversations.FirstOrDefaultAsync(c => c.Id == cid) : null;
        var posts = new List<TripChatPost>();
        await unitOfWork.BeginTransactionAsync();
        try
        {
            e.AdvanceStatus = TripAdvanceStatuses.Received;
            e.AdvanceReceivedAt = now;
            if (late)
            {
                e.Status = TripEnquiryStatuses.ConfirmedByBoth;
                e.FailReason = null;
            }
            e.UpdatedAt = now;
            await db.SaveChangesAsync();

            if (conversation != null)
            {
                await RewriteCardAsync(chat, conversation.Id, TripMessageTypes.AdvanceRequest, e.Id, TripCardStatuses.Received, posts);
                if (hadProof)
                    await RewriteCardAsync(chat, conversation.Id, TripMessageTypes.AdvanceProofSent, e.Id, TripCardStatuses.Received, posts);
                posts.Add(await chat.PostSystemAsync(conversation, callerId, TripSystemLines.AdvanceReceived,
                    TripSystemLines.Text(TripSystemLines.AdvanceReceived)));
            }
            await unitOfWork.CommitTransactionAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            await unitOfWork.RollbackTransactionAsync();
            return Concurrent();
        }
        catch (DbUpdateException ex) when (late && ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            // The guest re-opened an enquiry with this stay for the same night meanwhile.
            await unitOfWork.RollbackTransactionAsync();
            return ConflictResponse("The guest has booked another stay. Sort the advance with him directly.", TripEnquiryErrorTypes.TravellerMovedOn);
        }
        catch
        {
            await unitOfWork.RollbackTransactionAsync();
            throw;
        }

        await TripEnquiryHandlers.AfterCommitAsync(chat, posts,
            [new TripPendingPush(e, e.TravellerUserId, TripNotifications.TripAdvanceReceived)], unitOfWork, notifier, loggers);
        return OkResponse(await TripEnquiryDtoBuilder.BuildOneAsync(unitOfWork, e, callerId, now));
    }

    // ---------- POST /trips/enquiries/{id}/advance/not-yet (host) ----------

    public static async Task<IResult> NotYet(
        Guid id, ClaimsPrincipal principal, IUnitOfWork unitOfWork, ApplicationDbContext db, IRateLimitService rateLimiter,
        ITripChatService chat, ITripNotifier notifier, ILoggerFactory loggers, HttpContext http)
    {
        var (error, e, callerId) = await LoadForOwnerAsync(id, principal, unitOfWork, db, rateLimiter, http);
        if (error != null) return error;
        e = e!;

        var now = DateTime.UtcNow;
        if (e.Status != TripEnquiryStatuses.ConfirmedByBoth
            || e.AdvanceStatus is not (TripAdvanceStatuses.Pending or TripAdvanceStatuses.ProofSent))
            return NotAllowed();

        e.AdvanceNotYetAt = now;
        e.UpdatedAt = now;
        try { await unitOfWork.SaveChangesAsync(); }
        catch (DbUpdateConcurrencyException) { return Concurrent(); }

        // Status unchanged: the hourly reminders continue. The guest hears it at most once an hour.
        await TripEnquiryHandlers.AfterCommitAsync(chat, [],
            [new TripPendingPush(e, e.TravellerUserId, TripNotifications.TripAdvanceNotYet, DedupeKey: TripAdvanceRules.NotYetKey(e.Id, now))],
            unitOfWork, notifier, loggers);
        return OkResponse(await TripEnquiryDtoBuilder.BuildOneAsync(unitOfWork, e, callerId, now));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    // Caller, owner rate limit, tracked enquiry, owner side check (owner AND still owns the live stay).
    private static async Task<(IResult? Error, TripEnquiry? Enquiry, Guid CallerId)> LoadForOwnerAsync(
        Guid id, ClaimsPrincipal principal, IUnitOfWork unitOfWork, ApplicationDbContext db,
        IRateLimitService rateLimiter, HttpContext http)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var callerId)) return (UnauthorizedResponse(), null, Guid.Empty);

        if (await TripEnquiryHandlers.RateLimitedAsync(rateLimiter, http, $"trip:owner:{callerId}",
                TripExecutionRules.OwnerActionsPerHour, OwnerActionWindow) is { } limited)
            return (limited, null, callerId);

        var e = await unitOfWork.TripEnquiries.GetByIdTrackedAsync(id);
        if (await TripEnquiryAuth.RequireOwnerAsync(e, callerId, db) is { } denied) return (denied, null, callerId);
        return (null, e, callerId);
    }

    // Late mark: another booked stay of the same trip on any of these nights means the guest moved on.
    private static async Task<bool> MovedOnAsync(ApplicationDbContext db, TripEnquiry e)
    {
        if (e.TripId is not { } tripId || e.CheckIn is not { } checkIn || e.CheckOut is not { } checkOut) return true;
        var others = await db.TripEnquiries.AsNoTracking()
            .Where(x => x.TripId == tripId && x.Kind == TripEnquiryKinds.Stay && x.Id != e.Id
                && TripEnquiryStatuses.Booked.Contains(x.Status))
            .Select(x => new { x.CheckIn, x.CheckOut })
            .ToListAsync();
        if (others.Any(x => x.CheckIn is { } a && x.CheckOut is { } b && TripExecutionRules.NightsOverlap(a, b, checkIn, checkOut)))
            return true;
        // Back to ConfirmedByBoth must not collide with a newer open enquiry for this stay and night.
        return await db.TripEnquiries.AsNoTracking().AnyAsync(x => x.TripId == tripId && x.Kind == TripEnquiryKinds.Stay
            && x.Id != e.Id && x.ListingId == e.ListingId && x.Day == e.Day && TripEnquiryStatuses.Open.Contains(x.Status));
    }

    private static async Task RewriteCardAsync(
        ITripChatService chat, Guid conversationId, string type, Guid enquiryId, string status, List<TripChatPost> posts)
    {
        if (await chat.FindCardMessageIdAsync(conversationId, type, enquiryId) is not { } messageId) return;
        if (await chat.RewriteStatusAsync(messageId, status) is { } post) posts.Add(post);
    }

    private static string IsoDate(DateOnly? d) => d?.ToString("yyyy-MM-dd") ?? string.Empty;

    private static string? TenDigits(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return null;
        var digits = new string(phone.Where(char.IsDigit).ToArray());
        return digits.Length >= 10 ? digits[^10..] : digits;
    }
}
