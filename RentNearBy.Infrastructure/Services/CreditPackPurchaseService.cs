using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;

namespace RentNearBy.Infrastructure.Services;

public class CreditPackPurchaseService(IUnitOfWork unitOfWork, IRazorpayService razorpay, ICreditWalletService wallet, ILogger<CreditPackPurchaseService> logger)
    : ICreditPackPurchaseService
{
    private static readonly TimeSpan RecentSuccessWarningWindow = TimeSpan.FromMinutes(20);

    public async Task<CreatePaymentOrderResponse> CreateOrderAsync(Guid userId, Guid creditPackId, bool confirmed = false)
    {
        var pack = await unitOfWork.CreditPacks.GetByIdAsync(creditPackId);
        if (pack == null || !pack.IsEnabled)
            throw new ArgumentException("Credit pack not found or disabled.");

        var userPurchases = await unitOfWork.CreditPackPurchases.GetByUserIdAsync(userId);
        var existingPending = userPurchases.FirstOrDefault(p => p.Status == CreditPackPurchaseStatuses.Pending);

        // Resuming an already-started purchase takes priority over the recent-Success warning
        // below — if it didn't, a user with an unrelated in-progress Pending purchase (e.g. they
        // closed the app mid-checkout on a second, legitimate buy) would see a confusing "you
        // recently bought credits, buy again?" prompt when they're not buying again at all, just
        // finishing what they started. Checked first, before the recency check ever runs.
        //
        // Our own app-level reuse window — RazorpayService.CreateOrderAsync no longer sets
        // expire_by (this Razorpay account's settings reject that field outright), so the
        // underlying Razorpay order itself doesn't expire on any schedule we control. This
        // 20-minute check only decides whether WE keep offering the same pending purchase
        // back to the client vs. abandon it and mint a fresh one; same window the room/plot
        // Go-Live flow this replaces used for its own pending orders.
        if (existingPending != null && existingPending.CreatedAt > DateTime.UtcNow.AddMinutes(-20) &&
            !string.IsNullOrEmpty(existingPending.RazorpayOrderId))
        {
            return new CreatePaymentOrderResponse
            {
                OrderId = existingPending.RazorpayOrderId!,
                Amount = existingPending.PriceInr,
                Currency = "INR",
                KeyId = razorpay.GetKeyId(),
            };
        }

        // Soft warning, not a hard block — deliberately creates no order/DB row of its own, so
        // declining costs nothing to clean up. Guards the gap the Pending-reuse window above
        // doesn't cover: once a purchase has already flipped to Success (via the client's own
        // verify call, the webhook, or the reconciliation sweep), it's no longer Pending, so a
        // user who didn't see that confirmation (app killed mid-payment, or missed the toast)
        // could otherwise buy again unknowingly. Only reached once we know there's no immediately-
        // resumable Pending order (handled above). Window matches RecentSuccessWarningWindow/the
        // 20-min Pending-reuse window above for consistency.
        if (!confirmed)
        {
            var recentSuccess = userPurchases.FirstOrDefault(p =>
                p.Status == CreditPackPurchaseStatuses.Success &&
                p.CompletedAt.HasValue &&
                p.CompletedAt.Value > DateTime.UtcNow - RecentSuccessWarningWindow);
            if (recentSuccess != null)
                throw new InvalidOperationException(CreditPackPurchaseErrors.RecentPurchaseDetected);
        }

        if (existingPending != null)
        {
            // Reached only when existingPending is stale (fell through the resumable check above)
            // — supersede it. Atomic, status-guarded UPDATE — not a tracked-entity mutation +
            // SaveChangesAsync.
            // existingPending is otherwise unused after this (a brand-new purchase row is created
            // below regardless), so this was previously a blind overwrite: if a webhook, the client's
            // own delayed verify-call, or the reconciliation sweep (PendingCreditPurchaseCleanupService)
            // credits and flips this same row to Success in the moment between the read above and this
            // write, the blind write would have silently stomped that Success back to Abandoned
            // (credits already credited — idempotent, so no double-credit — but the purchase row itself
            // would permanently misreport a paid purchase as abandoned). Only actually abandons if the
            // row is still genuinely Pending at write time.
            await unitOfWork.CreditPackPurchases.MarkAbandonedIfPendingAsync(
                existingPending.Id, "Razorpay order expired — superseded by retry");
        }

        var purchase = new CreditPackPurchase
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            CreditPackId = pack.Id,
            Credits = pack.Credits,
            BonusCredits = pack.BonusCredits,
            PriceInr = pack.PriceInr,
            Status = CreditPackPurchaseStatuses.Pending,
            CreatedAt = DateTime.UtcNow,
        };

        var (orderId, returnedAmount) = await razorpay.CreateOrderAsync(pack.PriceInr, purchase.Id.ToString());
        if (returnedAmount != pack.PriceInr)
        {
            logger.LogError("Amount mismatch for credit pack purchase {PurchaseId}: expected {Expected}, got {Actual}", purchase.Id, pack.PriceInr, returnedAmount);
            throw new InvalidOperationException("Payment amount mismatch. Please try again.");
        }
        purchase.RazorpayOrderId = orderId;

        await unitOfWork.CreditPackPurchases.AddAsync(purchase);
        try
        {
            await unitOfWork.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Lost a race against a concurrent create-order call for this same user — the partial
            // unique index on PENDING credit-pack purchases caught it. Reuse whichever purchase
            // actually won, same pattern as the room/plot Go-Live flow this replaces.
            var winner = (await unitOfWork.CreditPackPurchases.GetByUserIdAsync(userId))
                .FirstOrDefault(p => p.Status == CreditPackPurchaseStatuses.Pending);
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
            throw new InvalidOperationException("A credit purchase is already in progress. Please try again.");
        }

        return new CreatePaymentOrderResponse
        {
            OrderId = orderId,
            Amount = pack.PriceInr,
            Currency = "INR",
            KeyId = razorpay.GetKeyId(),
        };
    }

    public async Task<CreditPackPurchaseVerifyResponse> VerifyAndCreditAsync(Guid userId, VerifyPaymentRequest request, bool skipSignatureCheck = false)
    {
        var purchase = await unitOfWork.CreditPackPurchases.GetByRazorpayOrderIdAsync(request.RazorpayOrderId);
        if (purchase == null) throw new KeyNotFoundException("Purchase not found.");
        if (purchase.UserId != userId) throw new UnauthorizedAccessException("Not your purchase.");
        if (purchase.Status == CreditPackPurchaseStatuses.Success) throw new InvalidOperationException(CreditPackPurchaseErrors.AlreadyProcessed);
        if (purchase.Status == CreditPackPurchaseStatuses.Failed && !skipSignatureCheck)
            throw new InvalidOperationException(CreditPackPurchaseErrors.PreviouslyFailed);

        if (!skipSignatureCheck && !razorpay.VerifyPaymentSignature(request.RazorpayOrderId, request.RazorpayPaymentId, request.RazorpaySignature))
        {
            // Atomic, status-guarded UPDATE — same reason as MarkCancelledIfPendingAsync/
            // MarkFailedIfPendingOrAbandonedAsync's other two call sites: a concurrent webhook that
            // credits and flips this same row to Success in the gap between the read above and this
            // write must not be silently stomped back to Failed.
            await unitOfWork.CreditPackPurchases.MarkFailedIfPendingOrAbandonedAsync(
                purchase.Id, request.RazorpayPaymentId, "Signature verification failed");
            throw new InvalidOperationException(CreditPackPurchaseErrors.SignatureInvalid);
        }

        // Two independent atomic steps, deliberately not one shared transaction: crediting the wallet
        // (Track 1's own atomic unit, idempotent via the ledger's one-shot dedup on
        // (UserId, RECHARGE, purchase.Id)) and flipping this purchase's own Status. A crash between
        // them is handled by PendingCreditPurchaseCleanupService's Pass A, not papered over here.
        var totalCredits = purchase.Credits + purchase.BonusCredits;
        var creditResult = await wallet.AddCreditsAsync(userId, totalCredits, CreditTransactionReasons.Recharge, purchase.Id);
        await unitOfWork.CreditPackPurchases.MarkSuccessIfPendingAsync(purchase.Id, request.RazorpayPaymentId, request.RazorpaySignature);

        return new CreditPackPurchaseVerifyResponse
        {
            Success = true,
            CreditsCredited = totalCredits,
            NewBalance = creditResult.BalanceAfter,
        };
    }

    private static readonly string[] StillResolvingStatuses = ["created", "authorized"];

    public async Task<CreditPackReconcileResult> ReconcileWithRazorpayAsync(CreditPackPurchase purchase)
    {
        IReadOnlyList<RazorpayPaymentAttempt> attempts;
        try
        {
            attempts = await razorpay.FetchPaymentsForOrderAsync(purchase.RazorpayOrderId!);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ReconcileWithRazorpayAsync: could not fetch payments for order {OrderId} (purchase {PurchaseId}) — leaving Pending", purchase.RazorpayOrderId, purchase.Id);
            return new CreditPackReconcileResult(CreditPackReconcileOutcome.Inconclusive);
        }

        // Pure elimination order — deliberately not "failed + none pending", which leaves an empty
        // items[] (checkout closed before any attempt) or a refunded payment in an undefined bucket.
        var captured = attempts.FirstOrDefault(a => a.Status == "captured");
        if (captured != null)
        {
            var request = new VerifyPaymentRequest
            {
                RazorpayOrderId = purchase.RazorpayOrderId!,
                RazorpayPaymentId = captured.Id,
                RazorpaySignature = string.Empty, // unused — skipSignatureCheck bypasses the client-signature gate
            };
            try
            {
                var response = await VerifyAndCreditAsync(purchase.UserId, request, skipSignatureCheck: true);
                return new CreditPackReconcileResult(CreditPackReconcileOutcome.Credited, response);
            }
            catch (InvalidOperationException ex)
            {
                // e.g. "Already processed" — the client's own verify-call or the webhook already won
                // this race. Still Credited, just nothing new to push.
                logger.LogInformation("ReconcileWithRazorpayAsync: purchase {PurchaseId} already settled by another path ({Message})", purchase.Id, ex.Message);
                return new CreditPackReconcileResult(CreditPackReconcileOutcome.Credited);
            }
        }

        if (attempts.Any(a => StillResolvingStatuses.Contains(a.Status)))
            return new CreditPackReconcileResult(CreditPackReconcileOutcome.Inconclusive);

        // Zero attempts at all, or every attempt is failed/refunded — safe to treat as not paid.
        // This is exhaustive against Razorpay's current documented status vocabulary (created|
        // authorized|captured|refunded|failed — see the RazorpayPaymentAttempt doc comment). If
        // Razorpay ever introduces a new intermediate status this doesn't recognize, it would fall
        // through to NotPaid rather than Inconclusive — revisit StillResolvingStatuses if that happens.
        return new CreditPackReconcileResult(CreditPackReconcileOutcome.NotPaid);
    }

    public async Task<LatestCreditPackPurchaseResponse> GetLatestPurchaseAsync(Guid userId)
    {
        var latest = (await unitOfWork.CreditPackPurchases.GetByUserIdAsync(userId)).FirstOrDefault();
        if (latest == null)
            return new LatestCreditPackPurchaseResponse { HasPurchase = false };

        return new LatestCreditPackPurchaseResponse
        {
            HasPurchase = true,
            Status = latest.Status,
            FailureReason = latest.FailureReason,
            CreatedAt = latest.CreatedAt,
            CompletedAt = latest.CompletedAt,
        };
    }
}
