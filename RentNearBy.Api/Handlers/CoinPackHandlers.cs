using System.Security.Claims;
using FluentValidation;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Memory;
using RentNearBy.Api.Hubs;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

public static class CoinPackHandlers
{
    public const string ActivePacksCacheKey = "active_coin_packs";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);

    public static async Task<IResult> GetActivePacks(IUnitOfWork unitOfWork, IMemoryCache cache)
    {
        if (!cache.TryGetValue(ActivePacksCacheKey, out List<CoinPackPublicDto>? cached) || cached == null)
        {
            var packs = await unitOfWork.CoinPacks.GetAllAsync();
            cached = packs
                .Where(p => p.IsEnabled)
                .OrderBy(p => p.SortOrder)
                .Select(p => new CoinPackPublicDto
                {
                    Id = p.Id,
                    Coins = p.Coins,
                    BonusCoins = p.BonusCoins,
                    TotalCoins = p.Coins + p.BonusCoins,
                    PriceInr = p.PriceInr,
                    IsFeatured = p.IsFeatured,
                    SortOrder = p.SortOrder,
                })
                .ToList();
            cache.Set(ActivePacksCacheKey, cached, CacheTtl);
        }

        return OkResponse(cached);
    }

    public static async Task<IResult> CreateOrder(
        CreateCoinPackOrderRequest request,
        IValidator<CreateCoinPackOrderRequest> validator,
        ClaimsPrincipal principal,
        ICoinPackPurchaseService purchaseService,
        IRateLimitService rateLimiter)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
            return BadRequestResponse(validation.Errors[0].ErrorMessage);

        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var rl = await rateLimiter.CheckAsync($"coinpack:create-order:{userId}", maxAttempts: 10, window: TimeSpan.FromMinutes(10));
        if (!rl.IsAllowed)
            return TooManyRequestsResponse();

        try
        {
            var response = await purchaseService.CreateOrderAsync(userId, request.CoinPackId);
            return OkResponse(response);
        }
        catch (ArgumentException) { return BadRequestResponse("Invalid coin pack."); }
        catch (InvalidOperationException ex) { return BadRequestResponse(ex.Message); }
        catch (Exception) { return ServerErrorResponse(); }
    }

    public static async Task<IResult> VerifyPayment(
        VerifyPaymentRequest request,
        ClaimsPrincipal principal,
        ICoinPackPurchaseService purchaseService,
        IHubContext<WalletHub> hubContext)
    {
        if (string.IsNullOrWhiteSpace(request.RazorpayOrderId) ||
            string.IsNullOrWhiteSpace(request.RazorpayPaymentId) ||
            string.IsNullOrWhiteSpace(request.RazorpaySignature))
            return BadRequestResponse("Missing payment verification details");

        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        try
        {
            var response = await purchaseService.VerifyAndCreditAsync(userId, request);

            // Scoped to just the push — a SignalR hiccup here must never fall through to the
            // catch (Exception) below and report a false failure for an already-credited purchase.
            try
            {
                await hubContext.Clients.Group($"user_{userId}").SendAsync("WalletBalanceChanged", new
                {
                    balance = response.NewBalance,
                    reason = CoinTransactionReasons.Recharge,
                    occurredAt = DateTime.UtcNow,
                });
            }
            catch { }

            return OkResponse(response);
        }
        catch (KeyNotFoundException) { return NotFoundResponse("Purchase not found."); }
        catch (UnauthorizedAccessException) { return UnauthorizedResponse(); }
        catch (InvalidOperationException ex) { return BadRequestResponse(ex.Message); }
        catch (Exception) { return ServerErrorResponse(); }
    }

    public record CancelOrderRequest(string RazorpayOrderId);

    public static async Task<IResult> CancelOrder(
        CancelOrderRequest request,
        ClaimsPrincipal principal,
        IUnitOfWork unitOfWork)
    {
        if (string.IsNullOrWhiteSpace(request.RazorpayOrderId))
            return BadRequestResponse("RazorpayOrderId is required");

        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var purchase = (await unitOfWork.CoinPackPurchases.GetByUserIdAsync(userId))
            .FirstOrDefault(p => p.RazorpayOrderId == request.RazorpayOrderId && p.Status == CoinPackPurchaseStatuses.Pending);

        if (purchase == null)
            return OkResponse(new { cancelled = false }); // idempotent — already settled or not found

        purchase.Status = CoinPackPurchaseStatuses.Cancelled;
        await unitOfWork.SaveChangesAsync();
        return OkResponse(new { cancelled = true });
    }
}
