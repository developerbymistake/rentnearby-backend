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
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

// A user's plans and purchases for the admin user screen, plus grant (idempotent) and revoke.
public static class AdminUserPlanHandlers
{
    private const int HistoryLimit = 50;

    public static async Task<IResult> GetUserPlans(Guid id, IUnitOfWork unitOfWork, ApplicationDbContext db)
    {
        if (await unitOfWork.Users.GetByIdAsync(id) == null) return NotFoundResponse("User not found");

        var history = await unitOfWork.UserPlans.GetHistoryAsync(id, HistoryLimit);
        var purchases = await unitOfWork.PlanPurchases.GetHistoryAsync(id, HistoryLimit);
        var now = DateTime.UtcNow;
        var current = history.FirstOrDefault(p => p.Status == UserPlanStatuses.Active && p.EndsAt > now);

        return OkResponse(new AdminUserPlansDto
        {
            Current = current == null ? null : ToDto(current),
            History = history.Select(ToDto).ToList(),
            Purchases = await AdminPurchaseHandlers.ToDtosAsync(db, purchases),
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
            await purchases.AdminRevokeAsync(adminId, id, request with { Reason = request.Reason.Trim() });
            return OkResponse(new { success = true });
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

    private static UserPlanDto ToDto(UserPlan p) => new()
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
    };
}
