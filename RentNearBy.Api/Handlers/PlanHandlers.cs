using System.Security.Claims;
using FluentValidation;
using Microsoft.AspNetCore.SignalR;
using RentNearBy.Api.Hubs;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

// /api/v1/plans: catalog, the caller's plan, quotes, prepaid Razorpay purchases and the one-time trial.
// All rules live in IPlanPurchaseService; these handlers only shape requests and map PlanServiceException.
public static class PlanHandlers
{
    public static async Task<IResult> GetCatalog(IPlanPurchaseService purchases, string? featureKey = null)
    {
        if (featureKey != null && !PlanFeatures.IsValid(featureKey))
            return BadRequestResponse("Unknown featureKey");
        return OkResponse(await purchases.GetCatalogAsync(featureKey));
    }

    public static async Task<IResult> GetMe(ClaimsPrincipal principal, IPlanPurchaseService purchases, HttpContext httpContext)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId)) return UnauthorizedResponse();
        return await Run(httpContext, async () => OkResponse(await purchases.GetMyPlanAsync(userId)));
    }

    public static async Task<IResult> Quote(
        QuoteRequest request, IValidator<QuoteRequest> validator, ClaimsPrincipal principal,
        IPlanPurchaseService purchases, HttpContext httpContext)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId)) return UnauthorizedResponse();
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage, PlanErrorTypes.InvalidRequest);
        return await Run(httpContext, async () => OkResponse(await purchases.QuoteAsync(userId, request)));
    }

    public static async Task<IResult> CreatePurchase(
        CreatePlanOrderRequest request, IValidator<CreatePlanOrderRequest> validator, ClaimsPrincipal principal,
        IPlanPurchaseService purchases, HttpContext httpContext)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId)) return UnauthorizedResponse();
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage, PlanErrorTypes.InvalidRequest);
        return await Run(httpContext, async () => OkResponse(await purchases.CreateOrderAsync(userId, request)));
    }

    // Client fast path after checkout; the webhook and the reconciler settle the same purchase idempotently.
    public static async Task<IResult> VerifyPurchase(
        Guid id, VerifyPlanPurchaseRequest request, IValidator<VerifyPlanPurchaseRequest> validator,
        ClaimsPrincipal principal, IPlanPurchaseService purchases, IHubContext<NotificationHub> hubContext,
        HttpContext httpContext)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId)) return UnauthorizedResponse();
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage, PlanErrorTypes.InvalidRequest);
        return await Run(httpContext, async () =>
        {
            var (status, notification) = await purchases.VerifyAsync(userId, id, request);
            if (notification != null)
                await AdminUserPlanHandlers.PushNotificationAsync(hubContext, notification);
            return OkResponse(status);
        });
    }

    public static async Task<IResult> GetPurchase(
        Guid id, ClaimsPrincipal principal, IPlanPurchaseService purchases, HttpContext httpContext)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId)) return UnauthorizedResponse();
        return await Run(httpContext, async () => OkResponse(await purchases.GetStatusAsync(userId, id)));
    }

    public static async Task<IResult> GetOpenPurchase(
        ClaimsPrincipal principal, IPlanPurchaseService purchases, HttpContext httpContext)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId)) return UnauthorizedResponse();
        return await Run(httpContext, async () => OkResponse(await purchases.GetOpenAsync(userId)));
    }

    public static async Task<IResult> GetPurchases(
        ClaimsPrincipal principal, IPlanPurchaseService purchases, HttpContext httpContext, int limit = 20)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId)) return UnauthorizedResponse();
        limit = Math.Clamp(limit, 1, 50);
        return await Run(httpContext, async () => OkResponse(await purchases.GetHistoryAsync(userId, limit)));
    }

    public static async Task<IResult> StartTrial(
        StartTrialRequest request, IValidator<StartTrialRequest> validator, ClaimsPrincipal principal,
        IPlanPurchaseService purchases, HttpContext httpContext)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId)) return UnauthorizedResponse();
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage, PlanErrorTypes.InvalidRequest);
        return await Run(httpContext, async () => OkResponse(await purchases.StartTrialAsync(userId, request.PlanId)));
    }

    private static async Task<IResult> Run(HttpContext httpContext, Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (PlanServiceException ex)
        {
            return PlanErrorResponse(ex, httpContext);
        }
    }
}
