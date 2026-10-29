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

// Plan purchases: keyset list (newest first), detail, recheck against Razorpay, full refund.
public static class AdminPurchaseHandlers
{
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

    // One Users query for the page's phones.
    internal static async Task<List<AdminPurchaseDto>> ToDtosAsync(ApplicationDbContext db, List<PlanPurchase> rows)
    {
        var ids = rows.Where(r => r.UserId != null).Select(r => r.UserId!.Value).Distinct().ToList();
        var phones = ids.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id))
                .Select(u => new { u.Id, u.PhoneNumber }).ToDictionaryAsync(u => u.Id, u => u.PhoneNumber);
        return rows.Select(r => PlanPurchaseService.ToAdminDto(
            r, r.UserId is { } uid ? phones.GetValueOrDefault(uid) : null)).ToList();
    }

    public static async Task<IResult> GetPurchaseById(Guid id, IUnitOfWork unitOfWork, ApplicationDbContext db)
    {
        var purchase = await unitOfWork.PlanPurchases.GetByIdAsync(id);
        if (purchase == null) return NotFoundResponse("Purchase not found");
        var events = purchase.RazorpayOrderId == null
            ? []
            : await unitOfWork.PaymentEvents.GetByOrderIdAsync(purchase.RazorpayOrderId, 20);
        return OkResponse(new
        {
            purchase = (await ToDtosAsync(db, [purchase]))[0],
            events = events.Select(e => new
            {
                e.EventId,
                e.EventType,
                e.Outcome,
                e.RazorpayPaymentId,
                e.ReceivedAt,
                e.ProcessedAt,
            }),
        });
    }

    public static async Task<IResult> RecheckPurchase(
        Guid id, IPlanPurchaseService purchases, IUnitOfWork unitOfWork, ApplicationDbContext db, HttpContext httpContext)
    {
        try
        {
            var outcome = await purchases.ReconcileOrderAsync(id, PurchaseSources.AdminRecheck);
            if (outcome == ReconcileOutcome.NotFound) return NotFoundResponse("Purchase not found");
            var purchase = await unitOfWork.PlanPurchases.GetByIdAsync(id);
            return OkResponse(new
            {
                outcome = outcome.ToString(),
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
        ClaimsPrincipal principal, IPlanPurchaseService purchases, HttpContext httpContext)
    {
        if (!AdminAuthHandlers.TryGetAdminId(principal, out var adminId)) return UnauthorizedResponse();
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);
        try
        {
            return OkResponse(await purchases.AdminRefundAsync(adminId, id, request.Reason.Trim()));
        }
        catch (PlanServiceException ex)
        {
            return PlanErrorResponse(ex, httpContext);
        }
    }
}
