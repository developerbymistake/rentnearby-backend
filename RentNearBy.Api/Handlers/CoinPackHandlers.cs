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
            var response = await purchaseService.CreateOrderAsync(userId, request.CoinPackId, request.Confirmed);
            return OkResponse(response);
        }
        catch (ArgumentException) { return BadRequestResponse("Invalid coin pack."); }
        catch (InvalidOperationException ex) when (ex.Message == CoinPackPurchaseErrors.RecentPurchaseDetected)
        {
            // No order/DB row was created for this attempt — soft warning only, client re-submits
            // with Confirmed:true to proceed.
            return ConflictResponse(ex.Message, "RECENT_PURCHASE_DETECTED");
        }
        catch (InvalidOperationException ex) { return BadRequestResponse(ex.Message); }
        catch (Exception) { return ServerErrorResponse(); }
    }

    public static async Task<IResult> GetLatestPurchase(
        ClaimsPrincipal principal,
        ICoinPackPurchaseService purchaseService)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var response = await purchaseService.GetLatestPurchaseAsync(userId);
        return OkResponse(response);
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
        catch (InvalidOperationException ex)
        {
            // Machine-readable type per condition — lets the client distinguish "already credited by
            // another path, treat as success" from a genuine hard rejection, instead of collapsing
            // all three into one indistinguishable 400.
            var type = ex.Message switch
            {
                CoinPackPurchaseErrors.AlreadyProcessed => "ALREADY_PROCESSED",
                CoinPackPurchaseErrors.PreviouslyFailed => "PURCHASE_ALREADY_FAILED",
                CoinPackPurchaseErrors.SignatureInvalid => "SIGNATURE_VERIFICATION_FAILED",
                _ => "BadRequest",
            };
            return BadRequestResponse(ex.Message, type);
        }
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

        var purchase = await unitOfWork.CoinPackPurchases.GetByRazorpayOrderIdAsync(request.RazorpayOrderId);
        if (purchase == null || purchase.UserId != userId)
            return OkResponse(new { cancelled = false }); // idempotent — already settled, not found, or not theirs

        // Atomic, status-guarded UPDATE (not a tracked-entity mutation + SaveChangesAsync) — a
        // concurrent webhook/verify-call that credits and flips this same row to Success in the gap
        // between the read above and this write must not be silently stomped back to Cancelled.
        var cancelled = await unitOfWork.CoinPackPurchases.MarkCancelledIfPendingAsync(purchase.Id);
        return OkResponse(new { cancelled });
    }
}
