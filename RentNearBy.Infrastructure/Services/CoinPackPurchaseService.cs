using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;

namespace RentNearBy.Infrastructure.Services;

public class CoinPackPurchaseService(IUnitOfWork unitOfWork, IRazorpayService razorpay, ICoinWalletService wallet, ILogger<CoinPackPurchaseService> logger)
    : ICoinPackPurchaseService
{
    public async Task<CreatePaymentOrderResponse> CreateOrderAsync(Guid userId, Guid coinPackId)
    {
        var pack = await unitOfWork.CoinPacks.GetByIdAsync(coinPackId);
        if (pack == null || !pack.IsEnabled)
            throw new ArgumentException("Coin pack not found or disabled.");

        var existingPending = (await unitOfWork.CoinPackPurchases.GetByUserIdAsync(userId))
            .FirstOrDefault(p => p.Status == CoinPackPurchaseStatuses.Pending);

        if (existingPending != null)
        {
            // Our own app-level reuse window — RazorpayService.CreateOrderAsync no longer sets
            // expire_by (this Razorpay account's settings reject that field outright), so the
            // underlying Razorpay order itself doesn't expire on any schedule we control. This
            // 20-minute check only decides whether WE keep offering the same pending purchase
            // back to the client vs. abandon it and mint a fresh one; same window the room/plot
            // Go-Live flow this replaces used for its own pending orders.
            if (existingPending.CreatedAt > DateTime.UtcNow.AddMinutes(-20) && !string.IsNullOrEmpty(existingPending.RazorpayOrderId))
            {
                return new CreatePaymentOrderResponse
                {
                    OrderId = existingPending.RazorpayOrderId!,
                    Amount = existingPending.PriceInr,
                    Currency = "INR",
                    KeyId = razorpay.GetKeyId(),
                };
            }

            existingPending.Status = CoinPackPurchaseStatuses.Abandoned;
            existingPending.FailureReason = "Razorpay order expired — superseded by retry";
            await unitOfWork.SaveChangesAsync();
        }

        var purchase = new CoinPackPurchase
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            CoinPackId = pack.Id,
            Coins = pack.Coins,
            BonusCoins = pack.BonusCoins,
            PriceInr = pack.PriceInr,
            Status = CoinPackPurchaseStatuses.Pending,
            CreatedAt = DateTime.UtcNow,
        };

        var (orderId, returnedAmount) = await razorpay.CreateOrderAsync(pack.PriceInr, purchase.Id.ToString());
        if (returnedAmount != pack.PriceInr)
        {
            logger.LogError("Amount mismatch for coin pack purchase {PurchaseId}: expected {Expected}, got {Actual}", purchase.Id, pack.PriceInr, returnedAmount);
            throw new InvalidOperationException("Payment amount mismatch. Please try again.");
        }
        purchase.RazorpayOrderId = orderId;

        await unitOfWork.CoinPackPurchases.AddAsync(purchase);
        try
        {
            await unitOfWork.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Lost a race against a concurrent create-order call for this same user — the partial
            // unique index on PENDING coin-pack purchases caught it. Reuse whichever purchase
            // actually won, same pattern as the room/plot Go-Live flow this replaces.
            var winner = (await unitOfWork.CoinPackPurchases.GetByUserIdAsync(userId))
                .FirstOrDefault(p => p.Status == CoinPackPurchaseStatuses.Pending);
            if (winner != null && !string.IsNullOrEmpty(winner.RazorpayOrderId))
            {
                return new CreatePaymentOrderResponse
                {
                    OrderId = winner.RazorpayOrderId!,
                    Amount = winner.PriceInr,
                    Currency = "INR",
                    KeyId = razorpay.GetKeyId(),
                };
            }
            throw new InvalidOperationException("A coin purchase is already in progress. Please try again.");
        }

        return new CreatePaymentOrderResponse
        {
            OrderId = orderId,
            Amount = pack.PriceInr,
            Currency = "INR",
            KeyId = razorpay.GetKeyId(),
        };
    }

    public async Task<CoinPackPurchaseVerifyResponse> VerifyAndCreditAsync(Guid userId, VerifyPaymentRequest request, bool skipSignatureCheck = false)
    {
        var purchase = await unitOfWork.CoinPackPurchases.GetByRazorpayOrderIdAsync(request.RazorpayOrderId);
        if (purchase == null) throw new KeyNotFoundException("Purchase not found.");
        if (purchase.UserId != userId) throw new UnauthorizedAccessException("Not your purchase.");
        if (purchase.Status == CoinPackPurchaseStatuses.Success) throw new InvalidOperationException("Already processed.");
        if (purchase.Status == CoinPackPurchaseStatuses.Failed && !skipSignatureCheck)
            throw new InvalidOperationException("Purchase previously failed. Please start a new one.");

        if (!skipSignatureCheck && !razorpay.VerifyPaymentSignature(request.RazorpayOrderId, request.RazorpayPaymentId, request.RazorpaySignature))
        {
            purchase.Status = CoinPackPurchaseStatuses.Failed;
            purchase.FailureReason = "Signature verification failed";
            await unitOfWork.SaveChangesAsync();
            throw new InvalidOperationException("Payment verification failed.");
        }

        // Two independent atomic steps, deliberately not one shared transaction: crediting the wallet
        // (Track 1's own atomic unit, idempotent via the ledger's one-shot dedup on
        // (UserId, RECHARGE, purchase.Id)) and flipping this purchase's own Status. A crash between
        // them is handled by PendingCoinPurchaseCleanupService's Pass A, not papered over here.
        var totalCoins = purchase.Coins + purchase.BonusCoins;
        var creditResult = await wallet.CreditCoinsAsync(userId, totalCoins, CoinTransactionReasons.Recharge, purchase.Id);
        await unitOfWork.CoinPackPurchases.MarkSuccessIfPendingAsync(purchase.Id, request.RazorpayPaymentId, request.RazorpaySignature);

        return new CoinPackPurchaseVerifyResponse
        {
            Success = true,
            CoinsCredited = totalCoins,
            NewBalance = creditResult.BalanceAfter,
        };
    }
}
