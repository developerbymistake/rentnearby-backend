using System.Security.Claims;
using FluentValidation;
using Microsoft.AspNetCore.SignalR;
using RentNearBy.Api.Hubs;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

// A user's plans and purchases for the admin user screen, plus grant (idempotent) and revoke.
public static class AdminUserPlanHandlers
{
    private const int HistoryLimit = 50;

    public static async Task<IResult> GetUserPlans(Guid id, IUnitOfWork unitOfWork, ApplicationDbContext db, IPlanCatalogCache catalog)
    {
        if (await unitOfWork.Users.GetByIdAsync(id) == null) return NotFoundResponse("User not found");

        var history = await unitOfWork.UserPlans.GetHistoryAsync(id, HistoryLimit);
        var purchases = await unitOfWork.PlanPurchases.GetHistoryAsync(id, HistoryLimit);
        var playSubs = await unitOfWork.PlaySubscriptions.GetByUserAsync(id, HistoryLimit);
        var now = DateTime.UtcNow;
        // A Play-owned row waiting for a late renewal sync is still the current plan.
        var current = history.FirstOrDefault(p => p.Status == UserPlanStatuses.Active && (p.EndsAt > now || p.PlaySubscriptionId != null));

        var referenced = history.Where(p => p.PlaySubscriptionId != null).Select(p => p.PlaySubscriptionId!.Value)
            .Where(sid => playSubs.All(s => s.Id != sid)).Distinct().ToList();
        var subsById = playSubs.ToDictionary(s => s.Id);
        if (referenced.Count > 0)
            foreach (var s in await unitOfWork.PlaySubscriptions.GetByIdsAsync(referenced))
                subsById[s.Id] = s;

        var snapshot = await catalog.GetAsync();
        UserPlanDto Map(UserPlan p) => ToDto(p, p.PlaySubscriptionId is { } sid ? subsById.GetValueOrDefault(sid) : null, snapshot);

        return OkResponse(new AdminUserPlansDto
        {
            Current = current == null ? null : Map(current),
            History = history.Select(Map).ToList(),
            Purchases = await AdminPurchaseHandlers.ToDtosAsync(db, purchases),
            PlaySubscriptions = playSubs.Select(s => new AdminPlaySubscriptionDto
            {
                Id = s.Id,
                TokenTail = PlaySyncLogEvents.Tail(s.PurchaseToken),
                ProductId = s.ProductId,
                BasePlanId = s.BasePlanId,
                State = s.State,
                MappingStatus = s.MappingStatus,
                DetachedReason = s.DetachedReason,
                ExpiryAt = s.ExpiryTime,
                AutoRenew = s.AutoRenewEnabled,
                AcknowledgedAt = s.AcknowledgedAt,
                IsTest = s.IsTestPurchase,
                CreatedAt = s.CreatedAt,
            }).ToList(),
        });
    }

    public static async Task<IResult> GrantPlan(
        Guid id, AdminGrantPlanRequest request, IValidator<AdminGrantPlanRequest> validator, ClaimsPrincipal principal,
        IPlanPurchaseService purchases, IHubContext<NotificationHub> hubContext, HttpContext httpContext)
    {
        if (!AdminAuthHandlers.TryGetAdminId(principal, out var adminId)) return UnauthorizedResponse();
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage, PlanErrorTypes.InvalidRequest);
        try
        {
            var result = await purchases.AdminGrantAsync(adminId, id, request);
            if (result.Notification != null)
                await PushNotificationAsync(hubContext, result.Notification);
            return OkResponse(new { purchase = result.Purchase, replayed = result.Replayed });
        }
        catch (PlanServiceException ex)
        {
            return PlanErrorResponse(ex, httpContext);
        }
    }

    public static async Task<IResult> RevokePlan(
        Guid id, AdminRevokePlanRequest request, IValidator<AdminRevokePlanRequest> validator, ClaimsPrincipal principal,
        IPlanPurchaseService purchases, HttpContext httpContext)
    {
        if (!AdminAuthHandlers.TryGetAdminId(principal, out var adminId)) return UnauthorizedResponse();
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage, PlanErrorTypes.InvalidRequest);
        try
        {
            var result = await purchases.AdminRevokeAsync(adminId, id, request with { Reason = request.Reason.Trim() });
            return OkResponse(new { success = true, refund = result.ProratedRefundQueued ? "prorated" : "none" });
        }
        catch (PlanServiceException ex)
        {
            return PlanErrorResponse(ex, httpContext);
        }
    }

    // SignalR only: the row was already published to notification.push by the plan service.
    internal static async Task PushNotificationAsync(IHubContext<NotificationHub> hubContext, NotificationEvent notification)
    {
        try
        {
            await hubContext.Clients.Group($"user_{notification.TargetUserId}").SendAsync("NotificationReceived", new
            {
                id = notification.Id,
                type = notification.Type,
                title = notification.Title,
                body = notification.Body,
                actionRoute = notification.ActionRoute,
            });
        }
        catch { }
    }

    private static UserPlanDto ToDto(UserPlan p, PlaySubscription? sub, PlanCatalogSnapshot snapshot) => new()
    {
        Id = p.Id,
        FeatureKey = p.FeatureKey,
        PlanId = p.PlanId,
        PlanName = p.Plan?.Name ?? string.Empty,
        Source = p.Source,
        Status = p.Status,
        StartsAt = p.StartsAt,
        EndsAt = p.EndsAt,
        TotalPaidInr = p.TotalPaidInr,
        TotalDays = p.TotalDays,
        GrantedByAdminId = p.GrantedByAdminId,
        Note = p.Note,
        CreatedAt = p.CreatedAt,
        CarryOverDays = p.CarryOverDays,
        Play = sub == null ? null : ToPlayDto(p, sub, snapshot),
    };

    private static AdminPlayDetailsDto ToPlayDto(UserPlan p, PlaySubscription s, PlanCatalogSnapshot snapshot) => new()
    {
        State = s.State,
        AutoRenewEnabled = s.AutoRenewEnabled,
        ExpiryAt = s.ExpiryTime,
        TrialEndsAt = p.PlayTrialEndsAt,
        ProductId = s.ProductId,
        BasePlanId = s.BasePlanId,
        OfferId = s.OfferId,
        ScheduledPlanName = p.PlayScheduledPlanId is { } scheduled ? snapshot.GetPlan(scheduled)?.Name : null,
        ScheduledAt = p.PlayScheduledPlanId != null ? p.EndsAt : null,
        IsTest = s.IsTestPurchase,
        Acknowledged = s.AcknowledgedAt != null || s.AcknowledgementState == PlayAckStates.Acknowledged,
        PausedAutoResumeAt = s.PausedAutoResumeAt,
        CancelReason = s.CancelReason,
        OwnsActivePlan = p.Status == UserPlanStatuses.Active && p.PlaySubscriptionId == s.Id,
        TokenTail = PlaySyncLogEvents.Tail(s.PurchaseToken),
        MappingStatus = s.MappingStatus,
        DetachedReason = s.DetachedReason,
        PendingServerAction = s.PendingServerAction,
        LastSyncedAt = s.LastSyncedAt,
        RecurringPriceInr = s.RecurringPriceMicros is { } micros && s.RecurringPriceCurrency == "INR"
            ? (int)Math.Round(micros / 1_000_000m, MidpointRounding.AwayFromZero)
            : null,
        PriceChangeState = s.PriceChangeState,
    };
}
