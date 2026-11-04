using System.Security.Claims;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

// Driver-side taxi lead endpoints: list, fare reply, not available, confirm / not me on a traveller booking.
public static class TripLeadHandlers
{
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 50;
    private const string DriverOnly = "Only the driver can do this.";
    private static readonly TimeSpan OwnerActionWindow = TimeSpan.FromHours(1);

    // GET /trips/leads/driver (policy CanListTaxis on the route)
    public static async Task<IResult> GetMine(
        string? status, int? page, int? pageSize, ClaimsPrincipal principal, IUnitOfWork unitOfWork)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var callerId)) return UnauthorizedResponse();

        var bucket = string.IsNullOrWhiteSpace(status) ? TripDriverBuckets.New : status.Trim().ToLowerInvariant();
        if (!TripDriverBuckets.All.Contains(bucket)) return BadRequestResponse("Status must be new, replied, booked or closed");
        var pageNo = Math.Max(1, page ?? 1);
        var size = Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize);

        var (items, hasMore) = await unitOfWork.TripEnquiries.GetDriverPagedAsync(callerId, bucket, pageNo, size);
        var counts = await unitOfWork.TripEnquiries.GetDriverCountsAsync(callerId);
        var built = await TripEnquiryViews.BuildAsync(unitOfWork, items, callerId);

        return OkResponse(new TripDriverLeadPageDto
        {
            Items = items.Where(i => built.ContainsKey(i.Id)).Select(i => built[i.Id]).ToList(),
            HasMore = hasMore,
            Counts = new TripDriverCountsDto { New = counts.New, Replied = counts.Replied, Booked = counts.Booked, Closed = counts.Closed },
        });
    }

    // POST /trips/leads/{id}/fare { fare, note }
    public static async Task<IResult> Fare(
        Guid id, DriverFareRequest request, IValidator<DriverFareRequest> validator, ClaimsPrincipal principal,
        IUnitOfWork unitOfWork, ApplicationDbContext db, IRateLimitService rateLimiter, ITripNotifier notifier,
        HttpContext httpContext, ILoggerFactory loggers)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var (error, e, callerId) = await LoadForDriverAsync(id, principal, unitOfWork, db, rateLimiter, httpContext);
        if (error != null) return error;
        e = e!;

        var now = DateTime.UtcNow;
        var fromAsked = e.Status == TripEnquiryStatuses.Asked;
        if (fromAsked)
        {
            if (e.ExpiresAt <= now) return Closed();
        }
        else if (e.Status == TripEnquiryStatuses.FareSent)
        {
            if (e.DriverFareEditCount >= TripLeadRules.MaxFareEdits)
                return ErrorResponse(409, "You can change the fare 3 times.", TripEnquiryErrorTypes.FareEditLimit);
        }
        else
        {
            return Closed();
        }

        var firstAnswer = fromAsked && now - e.CreatedAt <= TimeSpan.FromHours(TripLeadRules.AnsweredWithinHours);
        await unitOfWork.BeginTransactionAsync();
        try
        {
            e.DriverFare = request.Fare;
            e.DriverNote = TripTextSafety.CleanOrNull(request.Note, 300);
            e.DriverRepliedAt = now;
            if (fromAsked)
            {
                e.Status = TripEnquiryStatuses.FareSent;
                // A replied lead lives until the first trip day; a same-day lead keeps a short grace so it never expires at once.
                var firstDay = e.CheckIn is { } checkIn ? TripExecutionRules.IstMidnightUtc(checkIn) : now;
                var grace = now.AddHours(6);
                e.ExpiresAt = firstDay > grace ? firstDay : grace;
            }
            else
            {
                e.DriverFareEditCount++;
            }
            e.UpdatedAt = now;
            await unitOfWork.SaveChangesAsync();

            if (firstAnswer) await CountAnsweredAsync(db, e.ListingId);

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

        await PushAsync(unitOfWork, notifier, loggers, e, TripNotifications.TripDriverFare);
        return OkResponse(await TripEnquiryViews.BuildOneAsync(unitOfWork, e, callerId));
    }

    // POST /trips/leads/{id}/not-available
    public static async Task<IResult> NotAvailable(
        Guid id, ClaimsPrincipal principal, IUnitOfWork unitOfWork, ApplicationDbContext db, IRateLimitService rateLimiter,
        ITripNotifier notifier, HttpContext httpContext, ILoggerFactory loggers)
    {
        var (error, e, callerId) = await LoadForDriverAsync(id, principal, unitOfWork, db, rateLimiter, httpContext);
        if (error != null) return error;
        e = e!;

        if (e.Status == TripEnquiryStatuses.NotAvailable) return OkResponse(await TripEnquiryViews.BuildOneAsync(unitOfWork, e, callerId));
        if (e.Status is not (TripEnquiryStatuses.Asked or TripEnquiryStatuses.FareSent)) return Closed();

        var now = DateTime.UtcNow;
        var firstAnswer = e.Status == TripEnquiryStatuses.Asked && now - e.CreatedAt <= TimeSpan.FromHours(TripLeadRules.AnsweredWithinHours);
        await unitOfWork.BeginTransactionAsync();
        try
        {
            e.Status = TripEnquiryStatuses.NotAvailable;
            e.FailReason = null;
            e.DriverRepliedAt ??= now;
            e.UpdatedAt = now;
            await unitOfWork.SaveChangesAsync();

            if (firstAnswer) await CountAnsweredAsync(db, e.ListingId);

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

        await PushAsync(unitOfWork, notifier, loggers, e, TripNotifications.TripNotAvailable);
        return OkResponse(await TripEnquiryViews.BuildOneAsync(unitOfWork, e, callerId));
    }

    // POST /trips/leads/{id}/confirm
    public static async Task<IResult> Confirm(
        Guid id, ClaimsPrincipal principal, IUnitOfWork unitOfWork, ApplicationDbContext db, IRateLimitService rateLimiter,
        ITripNotifier notifier, HttpContext httpContext, ILoggerFactory loggers)
    {
        var (error, e, callerId) = await LoadForDriverAsync(id, principal, unitOfWork, db, rateLimiter, httpContext);
        if (error != null) return error;
        e = e!;

        if (e.Status == TripEnquiryStatuses.ConfirmedByBoth) return OkResponse(await TripEnquiryViews.BuildOneAsync(unitOfWork, e, callerId));
        if (e.Status != TripEnquiryStatuses.BookedByTraveller) return Closed();

        var now = DateTime.UtcNow;
        if (!await SaveAsync(unitOfWork, e, () =>
        {
            e.Status = TripEnquiryStatuses.ConfirmedByBoth;
            e.FailReason = null;
            e.ConfirmedByOwnerAt = now;
            e.UpdatedAt = now;
        })) return Concurrent();

        await PushAsync(unitOfWork, notifier, loggers, e, TripNotifications.TripDriverConfirmed);
        return OkResponse(await TripEnquiryViews.BuildOneAsync(unitOfWork, e, callerId));
    }

    // POST /trips/leads/{id}/deny
    public static async Task<IResult> Deny(
        Guid id, ClaimsPrincipal principal, IUnitOfWork unitOfWork, ApplicationDbContext db, IRateLimitService rateLimiter,
        ITripNotifier notifier, HttpContext httpContext, ILoggerFactory loggers)
    {
        var (error, e, callerId) = await LoadForDriverAsync(id, principal, unitOfWork, db, rateLimiter, httpContext);
        if (error != null) return error;
        e = e!;

        if (e.Status == TripEnquiryStatuses.Failed && e.FailReason == TripFailReasons.DriverDenied)
            return OkResponse(await TripEnquiryViews.BuildOneAsync(unitOfWork, e, callerId));
        if (e.Status != TripEnquiryStatuses.BookedByTraveller) return Closed();

        var now = DateTime.UtcNow;
        if (!await SaveAsync(unitOfWork, e, () =>
        {
            e.Status = TripEnquiryStatuses.Failed;
            e.FailReason = TripFailReasons.DriverDenied;
            e.ConfirmedByOwnerAt = null;
            e.UpdatedAt = now;
        })) return Concurrent();

        await PushAsync(unitOfWork, notifier, loggers, e, TripNotifications.TripDriverDenied);
        return OkResponse(await TripEnquiryViews.BuildOneAsync(unitOfWork, e, callerId));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    // Caller, rate limit, enquiry (tracked), and the driver side check: a Taxi row owned by the caller who still owns the live taxi.
    private static async Task<(IResult? Error, TripEnquiry? Enquiry, Guid CallerId)> LoadForDriverAsync(
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
        if (e == null) return (ErrorResponse(404, "Lead not found", TripEnquiryErrorTypes.TripEnquiryNotFound), null, callerId);

        var forbidden = ForbiddenResponse(DriverOnly, TripEnquiryErrorTypes.NotEnquiryOwner);
        if (e.Kind != TripEnquiryKinds.Taxi || e.OwnerUserId != callerId) return (forbidden, null, callerId);
        var owns = await db.TaxiListings.AsNoTracking().AnyAsync(t => t.Id == e.ListingId && t.UserId == callerId && !t.IsDeleted);
        return owns ? (null, e, callerId) : (forbidden, null, callerId);
    }

    // One short transaction for a plain state change.
    private static async Task<bool> SaveAsync(IUnitOfWork unitOfWork, TripEnquiry e, Action apply)
    {
        await unitOfWork.BeginTransactionAsync();
        try
        {
            apply();
            await unitOfWork.SaveChangesAsync();
            await unitOfWork.CommitTransactionAsync();
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            await unitOfWork.RollbackTransactionAsync();
            return false;
        }
        catch
        {
            await unitOfWork.RollbackTransactionAsync();
            throw;
        }
    }

    private static Task CountAnsweredAsync(ApplicationDbContext db, Guid taxiId) =>
        db.TaxiListings.Where(t => t.Id == taxiId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.TripLeadsAnsweredInApp, x => x.TripLeadsAnsweredInApp + 1));

    private static Task PushAsync(
        IUnitOfWork unitOfWork, ITripNotifier notifier, ILoggerFactory loggers, TripEnquiry e,
        Func<TripEnquiry, TripPushContext, NotificationEvent> build) =>
        TripPushSender.SendAsync([new TripPendingPush(e, e.TravellerUserId, build)], unitOfWork.TripEnquiries, notifier,
            loggers.CreateLogger("TripLead"));

    private static IResult Closed() =>
        ErrorResponse(409, "This lead is closed.", TripEnquiryErrorTypes.TripEnquiryClosed);

    private static IResult Concurrent() =>
        ErrorResponse(409, "This lead just changed.", TripEnquiryErrorTypes.ConcurrentUpdate);
}
