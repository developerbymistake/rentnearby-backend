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

// Plan purchases: keyset list (newest first), detail, recheck and full refund (Razorpay or Google Play by
// the row's provider), and the audited full Play token view.
public static class AdminPurchaseHandlers
{
    private const int EventLimit = 20;

    private static readonly string[] Statuses =
    [
        PurchaseStatuses.Created, PurchaseStatuses.Pending, PurchaseStatuses.Paid, PurchaseStatuses.Granted,
        PurchaseStatuses.Failed, PurchaseStatuses.Expired, PurchaseStatuses.Refunded,
    ];

    public static async Task<IResult> GetPurchases(
        IUnitOfWork unitOfWork, ApplicationDbContext db,
        string? status = null, Guid? userId = null, DateTime? afterCreatedAt = null, Guid? afterId = null, int limit = 50)
    {
        if (status != null && !Statuses.Contains(status)) return BadRequestResponse("Unknown status");
        if (afterCreatedAt.HasValue != afterId.HasValue)
            return BadRequestResponse("afterCreatedAt and afterId must be sent together");
        limit = Math.Clamp(limit, 1, 100);

        var afterUtc = afterCreatedAt.HasValue ? DateTime.SpecifyKind(afterCreatedAt.Value.ToUniversalTime(), DateTimeKind.Utc) : (DateTime?)null;
        var rows = await unitOfWork.PlanPurchases.GetAdminPageAsync(status, userId, afterUtc, afterId, limit + 1);
        var hasMore = rows.Count > limit;
        if (hasMore) rows = rows.Take(limit).ToList();

        var items = await ToDtosAsync(db, rows);
        var last = rows.LastOrDefault();
        return OkResponse(new
        {
            items,
            hasMore,
            nextAfterCreatedAt = hasMore ? last?.CreatedAt : null,
            nextAfterId = hasMore ? last?.Id : null,
        });
    }

    // One Users query for the page's phones, one PlaySubscriptions query for the page's Play rows.
    internal static async Task<List<AdminPurchaseDto>> ToDtosAsync(ApplicationDbContext db, List<PlanPurchase> rows)
    {
        var ids = rows.Where(r => r.UserId != null).Select(r => r.UserId!.Value).Distinct().ToList();
        var phones = ids.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id))
                .Select(u => new { u.Id, u.PhoneNumber }).ToDictionaryAsync(u => u.Id, u => u.PhoneNumber);

        var subIds = rows.Where(r => r.PlaySubscriptionId != null).Select(r => r.PlaySubscriptionId!.Value).Distinct().ToList();
        var subs = subIds.Count == 0
            ? new Dictionary<Guid, PlaySubscription>()
            : await db.PlaySubscriptions.AsNoTracking().Where(s => subIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id);

        return rows.Select(r =>
        {
            var dto = PlanPurchaseService.ToAdminDto(r, r.UserId is { } uid ? phones.GetValueOrDefault(uid) : null);
            dto.PlayOrderId = r.PlayOrderId;
            dto.PlayProductId = r.PlayProductId;
            dto.PlayBasePlanId = r.PlayBasePlanId;
            dto.PlayOfferId = r.PlayOfferId;
            dto.PlaySubscriptionId = r.PlaySubscriptionId;
            dto.PlayChargebackReviewAt = r.PlayChargebackReviewAt;
            if (r.PlaySubscriptionId is { } sid && subs.TryGetValue(sid, out var s))
            {
                dto.PlayTokenTail = PlaySyncLogEvents.Tail(s.PurchaseToken);
                dto.PlayState = s.State;
                dto.PlayExpiryAt = s.ExpiryTime;
                dto.PlayAutoRenew = s.AutoRenewEnabled;
                dto.PlayAcknowledgedAt = s.AcknowledgedAt;
                dto.PlayIsTest = s.IsTestPurchase;
                dto.PlayMappingStatus = s.MappingStatus;
                dto.PlayDetachedReason = s.DetachedReason;
            }
            return dto;
        }).ToList();
    }

    public static async Task<IResult> GetPurchaseById(Guid id, IUnitOfWork unitOfWork, ApplicationDbContext db)
    {
        var purchase = await unitOfWork.PlanPurchases.GetByIdAsync(id);
        if (purchase == null) return NotFoundResponse("Purchase not found");

        object events;
        if (purchase.Provider == PaymentProviders.Play)
        {
            var token = purchase.PlaySubscriptionId == null ? null : await db.PlaySubscriptions.AsNoTracking()
                .Where(s => s.Id == purchase.PlaySubscriptionId)
                .Select(s => s.PurchaseToken)
                .FirstOrDefaultAsync();
            var playEvents = await unitOfWork.PaymentEvents.GetByPlayTokenOrOrderAsync(token, purchase.PlayOrderId, EventLimit);
            events = playEvents.Select(e => new
            {
                e.EventId,
                e.EventType,
                e.Outcome,
                e.PlayNotificationType,
                e.PlayOrderId,
                e.ReceivedAt,
                e.ProcessedAt,
            }).ToList();
        }
        else
        {
            var razorpayEvents = purchase.RazorpayOrderId == null
                ? []
                : await unitOfWork.PaymentEvents.GetByOrderIdAsync(purchase.RazorpayOrderId, EventLimit);
            events = razorpayEvents.Select(e => new
            {
                e.EventId,
                e.EventType,
                e.Outcome,
                e.RazorpayPaymentId,
                e.ReceivedAt,
                e.ProcessedAt,
            }).ToList();
        }

        return OkResponse(new
        {
            purchase = (await ToDtosAsync(db, [purchase]))[0],
            events,
        });
    }

    public static async Task<IResult> GetPlayToken(
        Guid id, ClaimsPrincipal principal, IUnitOfWork unitOfWork, ApplicationDbContext db, ILoggerFactory loggerFactory)
    {
        if (!AdminAuthHandlers.TryGetAdminId(principal, out var adminId)) return UnauthorizedResponse();
        var purchase = await unitOfWork.PlanPurchases.GetByIdAsync(id);
        if (purchase == null) return NotFoundResponse("Purchase not found");
        var token = purchase.PlaySubscriptionId == null ? null : await db.PlaySubscriptions.AsNoTracking()
            .Where(s => s.Id == purchase.PlaySubscriptionId)
            .Select(s => s.PurchaseToken)
            .FirstOrDefaultAsync();
        if (token == null) return NotFoundResponse("This purchase has no Google Play token");

        loggerFactory.CreateLogger(nameof(AdminPurchaseHandlers)).LogInformation(PlaySyncLogEvents.AdminTokenViewed,
            "Admin {AdminId} viewed the Play token {TokenTail} of purchase {PurchaseId}", adminId, PlaySyncLogEvents.Tail(token), id);
        return OkResponse(new { purchaseToken = token });
    }

    public static async Task<IResult> RecheckPurchase(
        Guid id, IPlanPurchaseService purchases, IPlayBillingSyncService playSync, IUnitOfWork unitOfWork,
        ApplicationDbContext db, HttpContext httpContext)
    {
        try
        {
            var existing = await unitOfWork.PlanPurchases.GetByIdAsync(id);
            if (existing == null) return NotFoundResponse("Purchase not found");

            string outcome;
            if (existing.Provider == PaymentProviders.Play)
            {
                var token = existing.PlaySubscriptionId == null ? null : await db.PlaySubscriptions.AsNoTracking()
                    .Where(s => s.Id == existing.PlaySubscriptionId)
                    .Select(s => s.PurchaseToken)
                    .FirstOrDefaultAsync();
                if (token == null)
                    return ConflictResponse("This order never reached Google Play; nothing to sync.", PlanErrorTypes.InvalidRequest);
                outcome = (await playSync.SyncTokenAsync(new PlaySyncRequest(token, PurchaseSources.AdminRecheck))).Outcome.ToString();
            }
            else
            {
                var result = await purchases.ReconcileOrderAsync(id, PurchaseSources.AdminRecheck);
                if (result == ReconcileOutcome.NotFound) return NotFoundResponse("Purchase not found");
                outcome = result.ToString();
            }

            var purchase = await unitOfWork.PlanPurchases.GetByIdAsync(id);
            return OkResponse(new
            {
                outcome,
                purchase = purchase == null ? null : (await ToDtosAsync(db, [purchase]))[0],
            });
        }
        catch (PlanServiceException ex)
        {
            return PlanErrorResponse(ex, httpContext);
        }
    }

    public static async Task<IResult> RefundPurchase(
        Guid id, AdminRefundPurchaseRequest request, IValidator<AdminRefundPurchaseRequest> validator,
        ClaimsPrincipal principal, IPlanPurchaseService purchases, IPlayBillingSyncService playSync,
        IUnitOfWork unitOfWork, ApplicationDbContext db, HttpContext httpContext)
    {
        if (!AdminAuthHandlers.TryGetAdminId(principal, out var adminId)) return UnauthorizedResponse();
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);
        try
        {
            var existing = await unitOfWork.PlanPurchases.GetByIdAsync(id);
            if (existing == null) return NotFoundResponse("Purchase not found");
            if (existing.Provider == PaymentProviders.Play)
                await playSync.AdminRefundPlayAsync(adminId, id, request.Reason.Trim());
            else
                await purchases.AdminRefundAsync(adminId, id, request.Reason.Trim());

            var purchase = await unitOfWork.PlanPurchases.GetByIdAsync(id);
            return OkResponse((await ToDtosAsync(db, [purchase!]))[0]);
        }
        catch (PlanServiceException ex)
        {
            return PlanErrorResponse(ex, httpContext);
        }
    }
}
