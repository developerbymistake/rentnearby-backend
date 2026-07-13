using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using StackExchange.Redis;

namespace RentNearBy.Infrastructure.Services;

public interface IPaymentService
{
    Task<CreatePaymentOrderResponse> CreateOrderAsync(Guid userId, Guid listingId, string planType);
    Task<PaymentInitiateResponse> InitiatePaymentAsync(Guid userId, Guid listingId, string planType);
    Task<PaymentVerifyResponse> VerifyAndActivateAsync(Guid userId, VerifyPaymentRequest request, bool skipSignatureCheck = false);
    Task<bool> CanUserActivateListingAsync(Guid userId);
    Task<int> GetActiveRoomCountAsync(Guid userId);
    Task<CreatePaymentOrderResponse> CreateUpgradeOrderAsync(Guid userId, string planType);
    Task<PaymentVerifyResponse> VerifyUpgradePaymentAsync(Guid userId, VerifyPaymentRequest request, bool skipSignatureCheck = false);

    // PlotListing payment methods
    Task<CreatePaymentOrderResponse> CreatePlotListingOrderAsync(Guid userId, Guid plotId, string planType);
    Task<PlotListingPaymentVerifyResponse> VerifyPlotListingPaymentAsync(Guid userId, VerifyPaymentRequest request, bool skipSignatureCheck = false);
    Task<bool> CanUserActivatePlotListingAsync(Guid userId);
    Task<int> GetActivePlotListingCountAsync(Guid userId);
    Task<CreatePaymentOrderResponse> CreatePlotListingUpgradeOrderAsync(Guid userId, string planType);
    Task<PlotListingPaymentVerifyResponse> VerifyPlotListingUpgradePaymentAsync(Guid userId, VerifyPaymentRequest request, bool skipSignatureCheck = false);
}

public class PaymentService : IPaymentService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IRazorpayService _razorpay;
    private readonly ILogger<PaymentService> _logger;
    private readonly IConnectionMultiplexer _redis;

    public PaymentService(IUnitOfWork unitOfWork, IRazorpayService razorpay, ILogger<PaymentService> logger, IConnectionMultiplexer redis)
    {
        _unitOfWork = unitOfWork;
        _razorpay = razorpay;
        _logger = logger;
        _redis = redis;
    }

    // Returns whichever membership it deactivated (or null) so callers can extend the new
    // membership's ValidUntil from it, instead of always resetting to "now" — see PaymentService
    // audit notes: a renewal/upgrade before the current plan expires should add to the
    // remaining time, not discard it.
    private async Task<RoomMembership?> DeactivateExistingMembershipsAsync(Guid userId)
    {
        var existing = await _unitOfWork.RoomMemberships.GetActiveByUserIdAsync(userId);
        if (existing != null)
        {
            existing.IsActive = false;
            existing.UpdatedAt = DateTime.UtcNow;
        }
        return existing;
    }

    private async Task InvalidateNearbyCacheAsync(Guid? districtId)
    {
        if (districtId == null) return;
        try
        {
            var db = _redis.GetDatabase();
            var server = _redis.GetServer(_redis.GetEndPoints()[0]);
            var keys = server.Keys(pattern: $"nearby:{districtId}:*").ToArray();
            if (keys.Length > 0) await db.KeyDeleteAsync(keys);
        }
        catch { }
    }

    private async Task InvalidatePlotListingNearbyCacheAsync(Guid? districtId)
    {
        if (districtId == null) return;
        try
        {
            var db = _redis.GetDatabase();
            var server = _redis.GetServer(_redis.GetEndPoints()[0]);
            var keys = server.Keys(pattern: $"nearby_plot:{districtId}:*").ToArray();
            if (keys.Length > 0) await db.KeyDeleteAsync(keys);
        }
        catch { }
    }

    public async Task<CreatePaymentOrderResponse> CreateOrderAsync(Guid userId, Guid listingId, string planType)
    {
        // Validate plan exists and is enabled — routing is by plan.Price, not plan type name
        var plan = await _unitOfWork.RoomPlans.GetByPlanTypeAsync(planType);
        if (plan == null || !plan.IsEnabled)
            throw new ArgumentException($"RoomPlan '{planType}' does not exist or is disabled.");
        bool isFree = plan.OriginalPrice == 0;

        var paymentFeature = await _unitOfWork.Features.GetByKeyAsync(FeatureKeys.RoomPayment);
        if (paymentFeature == null)
            throw new InvalidOperationException("Payment feature not configured.");

        if (!paymentFeature.IsEnabled && !isFree)
            throw new InvalidOperationException("Payment feature is not enabled yet.");

        var listing = await _unitOfWork.RoomListings.GetByIdAsync(listingId);
        if (listing == null)
            throw new KeyNotFoundException("RoomListing not found.");
        if (listing.UserId != userId)
            throw new UnauthorizedAccessException("You don't own this listing.");

        _logger.LogInformation($"Creating order for {planType} plan, user {userId}, listing {listingId}");

        // Handle existing PENDING transactions
        var existingPendingTransaction = (await _unitOfWork.PaymentTransactions.GetByUserIdAsync(userId))
            .FirstOrDefault(t => t.RoomListingId == listingId && t.Status == "PENDING");

        if (existingPendingTransaction != null)
        {
            _logger.LogInformation($"Existing PENDING transaction {existingPendingTransaction.Id} found for listing {listingId}, plan {planType}");

            if (isFree)
            {
                // Reuse existing PENDING transaction — activate it now
                await ActivateZeroPricePlanAsync(userId, existingPendingTransaction.Id);
                return new CreatePaymentOrderResponse
                {
                    OrderId = existingPendingTransaction.Id.ToString(),
                    Amount = 0,
                    Currency = "INR",
                    KeyId = string.Empty
                };
            }
            else // requires Razorpay payment
            {
                if (!string.IsNullOrEmpty(existingPendingTransaction.RazorpayOrderId))
                {
                    // Matches the expire_by set on the Razorpay order itself (RazorpayService.
                    // CreateOrderAsync) — reuse only within that same window.
                    if (existingPendingTransaction.CreatedAt > DateTime.UtcNow.AddMinutes(-20))
                    {
                        return new CreatePaymentOrderResponse
                        {
                            OrderId = existingPendingTransaction.RazorpayOrderId!,
                            Amount = plan.OriginalPrice,
                            Currency = "INR",
                            KeyId = _razorpay.GetKeyId()
                        };
                    }
                    // Order presumed expired: mark ABANDONED (not FAILED) so a fresh order can be
                    // created. Same reasoning as PendingPaymentCleanupService — this is a timeout
                    // presumption, not a genuine rejection; if the ORIGINAL payment's
                    // payment.captured event still arrives late, it must remain recoverable
                    // (ABANDONED falls through both the webhook's and the verify methods' terminal
                    // checks) rather than permanently discarding a plan the user actually paid for.
                    existingPendingTransaction.Status = "ABANDONED";
                    existingPendingTransaction.FailureReason = "Razorpay order expired — superseded by retry";
                    await _unitOfWork.SaveChangesAsync();
                }
            }
        }

        var txUser = await _unitOfWork.Users.GetByIdAsync(userId);

        // Free plans can only be used once per user (when payment feature is enabled)
        if (isFree && paymentFeature.IsEnabled)
        {
            if (txUser?.HasUsedFreePlan == true)
            {
                _logger.LogWarning($"User {userId} attempted to reuse free plan");
                throw new InvalidOperationException("You have already used the free plan. Please use the paid plan.");
            }
        }

        var amount = plan.OriginalPrice;
        var transaction = new PaymentTransaction
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            PhoneNumber = txUser?.PhoneNumber ?? string.Empty,
            RoomListingId = listingId,
            PlanType = planType,
            Amount = amount,
            Status = "PENDING",
            CreatedAt = DateTime.UtcNow
        };

        // For paid plans: Create Razorpay order first
        if (!isFree)
        {
            var (orderId, returnedAmount) = await _razorpay.CreateOrderAsync(amount, transaction.Id.ToString());

            if (returnedAmount != amount)
            {
                _logger.LogError($"Amount mismatch for transaction {transaction.Id}: expected {amount}, got {returnedAmount}");
                throw new InvalidOperationException("Payment amount mismatch. Please try again.");
            }

            transaction.RazorpayOrderId = orderId;
            _logger.LogInformation($"Razorpay order created: {orderId} for amount {amount}");
        }

        // Save transaction to DB BEFORE activating (critical for ActivateZeroPricePlanAsync to find it)
        await _unitOfWork.PaymentTransactions.AddAsync(transaction);
        try
        {
            await _unitOfWork.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Lost a race against a concurrent create-order call for this same listing — the
            // partial unique index on PENDING room-listing transactions caught it. Reuse
            // whichever transaction actually won, same as the "existing pending" branch above.
            // The Razorpay order this losing request just created is simply never handed to
            // the client and goes unpaid — harmless, Razorpay doesn't charge for unpaid orders.
            var winner = (await _unitOfWork.PaymentTransactions.GetByUserIdAsync(userId))
                .FirstOrDefault(t => t.RoomListingId == listingId && t.Status == "PENDING");
            if (winner != null && !isFree && !string.IsNullOrEmpty(winner.RazorpayOrderId))
            {
                return new CreatePaymentOrderResponse
                {
                    OrderId = winner.RazorpayOrderId!,
                    Amount = plan.OriginalPrice,
                    Currency = "INR",
                    KeyId = _razorpay.GetKeyId()
                };
            }
            throw new InvalidOperationException("Payment order already exists for this listing. Please try again.");
        }

        // For free plans: Auto-activate immediately (now transaction is in DB)
        if (isFree)
        {
            _logger.LogInformation($"Auto-activating {planType} plan for user {userId}, listing {listingId}");
            await ActivateZeroPricePlanAsync(userId, transaction.Id);

            return new CreatePaymentOrderResponse
            {
                OrderId = transaction.Id.ToString(),
                Amount = amount,
                Currency = "INR",
                KeyId = string.Empty
            };
        }

        // For paid plans: Return Razorpay order details
        return new CreatePaymentOrderResponse
        {
            OrderId = transaction.RazorpayOrderId!,
            Amount = amount,
            Currency = "INR",
            KeyId = _razorpay.GetKeyId()
        };
    }

    public async Task<PaymentInitiateResponse> InitiatePaymentAsync(Guid userId, Guid listingId, string planType)
    {
        // Validate plan exists and is enabled
        var plan = await _unitOfWork.RoomPlans.GetByPlanTypeAsync(planType);
        if (plan == null || !plan.IsEnabled)
            throw new ArgumentException($"RoomPlan '{planType}' does not exist or is disabled.");
        bool isFree = plan.OriginalPrice == 0;

        var listing = await _unitOfWork.RoomListings.GetByIdAsync(listingId);
        if (listing == null)
            throw new KeyNotFoundException("RoomListing not found.");
        if (listing.UserId != userId)
            throw new UnauthorizedAccessException("You don't own this listing.");

        var paymentFeature = await _unitOfWork.Features.GetByKeyAsync(FeatureKeys.RoomPayment);
        if (paymentFeature == null)
            throw new InvalidOperationException("Payment feature not configured.");

        if (!paymentFeature.IsEnabled && !isFree)
            throw new InvalidOperationException("Payment feature is not enabled yet.");

        _logger.LogInformation($"Initiating {planType} payment for user {userId}, listing {listingId}");

        // Prevent duplicate transactions
        var existingPendingTransaction = (await _unitOfWork.PaymentTransactions.GetByUserIdAsync(userId))
            .FirstOrDefault(t => t.RoomListingId == listingId && t.Status == "PENDING");

        if (existingPendingTransaction != null)
        {
            // Auto-cancel any abandoned paid transaction — user chose free plan, respect that decision
            _logger.LogInformation($"Auto-cancelling PENDING transaction {existingPendingTransaction.Id} for user {userId} (chose free plan)");
            existingPendingTransaction.Status = "CANCELLED";
        }

        var txUser2 = await _unitOfWork.Users.GetByIdAsync(userId);

        // Free plans can only be used once per user (when payment feature is enabled)
        if (isFree && paymentFeature.IsEnabled)
        {
            if (txUser2?.HasUsedFreePlan == true)
            {
                _logger.LogWarning($"User {userId} attempted to use free plan again");
                throw new InvalidOperationException("You have already used the free plan. Please use the paid plan.");
            }
        }

        var amount = plan.OriginalPrice;
        var transaction = new PaymentTransaction
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            PhoneNumber = txUser2?.PhoneNumber ?? string.Empty,
            RoomListingId = listingId,
            PlanType = planType,
            Amount = amount,
            Status = "PENDING",
            CreatedAt = DateTime.UtcNow
        };

        if (!isFree)
        {
            var (orderId, returnedAmount) = await _razorpay.CreateOrderAsync(amount, transaction.Id.ToString());

            if (returnedAmount != amount)
            {
                _logger.LogError($"Amount mismatch for transaction {transaction.Id}: expected {amount}, got {returnedAmount}");
                throw new InvalidOperationException("Payment amount mismatch. Please try again.");
            }

            transaction.RazorpayOrderId = orderId;
            _logger.LogInformation($"Razorpay order created: {orderId} for amount {amount}");
        }

        await _unitOfWork.PaymentTransactions.AddAsync(transaction);
        try
        {
            await _unitOfWork.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Lost a race against a concurrent request for this same listing (this method
            // creates the same room-listing-PENDING shape as CreateOrderAsync, so it's subject
            // to the same partial unique index). Reuse whichever transaction actually won.
            var winner = (await _unitOfWork.PaymentTransactions.GetByUserIdAsync(userId))
                .FirstOrDefault(t => t.RoomListingId == listingId && t.Status == "PENDING");
            // Guard against the winner being a FREE-plan transaction (neither this method's nor
            // CreateOrderAsync's "existing pending" lookup filters by plan type, so a free-plan
            // pending row and a paid-plan request can collide on the same RoomListingId key) —
            // a free transaction never has a RazorpayOrderId, which would otherwise hand this
            // paying user a response with a null order to check out against.
            if (winner != null && !isFree && !string.IsNullOrEmpty(winner.RazorpayOrderId))
            {
                return new PaymentInitiateResponse
                {
                    TransactionId = winner.Id,
                    RazorpayOrderId = winner.RazorpayOrderId,
                    Amount = amount,
                    PlanType = planType,
                    Currency = "INR"
                };
            }
            throw new InvalidOperationException("Payment order already exists for this listing. Please try again.");
        }

        _logger.LogInformation($"Payment transaction initiated: {transaction.Id} for {planType} plan");

        // Auto-activate free plans immediately (no Razorpay verification needed)
        if (isFree)
        {
            _logger.LogInformation($"Auto-activating {planType} plan for user {userId}, transaction {transaction.Id}");
            try
            {
                await ActivateZeroPricePlanAsync(userId, transaction.Id);
                _logger.LogInformation($"{planType} plan auto-activated for user {userId}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error auto-activating {planType} plan: {ex.Message}", ex);
                throw;
            }
        }

        return new PaymentInitiateResponse
        {
            TransactionId = transaction.Id,
            RazorpayOrderId = transaction.RazorpayOrderId,
            Amount = amount,
            PlanType = planType,
            Currency = "INR"
        };
    }

    public async Task<PaymentVerifyResponse> VerifyAndActivateAsync(Guid userId, VerifyPaymentRequest request, bool skipSignatureCheck = false)
    {
        _logger.LogInformation($"Verifying payment for user {userId}, order {request.RazorpayOrderId}");

        var transaction = await _unitOfWork.PaymentTransactions.GetByRazorpayOrderIdAsync(request.RazorpayOrderId);
        if (transaction == null)
        {
            _logger.LogWarning($"Transaction not found for order {request.RazorpayOrderId}");
            throw new KeyNotFoundException("Transaction not found.");
        }

        if (transaction.UserId != userId)
        {
            _logger.LogWarning($"User {userId} attempted to verify transaction {transaction.Id} owned by {transaction.UserId}");
            throw new UnauthorizedAccessException("Transaction doesn't belong to you.");
        }

        // Only a genuine room-fresh-purchase transaction ever has RoomListingId set (room
        // upgrades and any plot transaction never set it) — without this, a user could submit a
        // different, genuinely-paid transaction's credentials (e.g. a plot purchase, or a room
        // upgrade) straight to this endpoint. Room and plot plans share identical PlanType
        // strings (both seed "BASIC"/"STANDARD"), so the plan lookup below would still resolve
        // and the signature would still verify (it's a real Razorpay signature) — silently
        // activating the wrong kind of membership while permanently stranding the transaction's
        // real, intended purchase (marked SUCCESS here, so the correct verify endpoint would
        // then reject it as already processed).
        if (transaction.RoomListingId == null)
        {
            _logger.LogWarning($"Transaction {transaction.Id} is not a room-listing payment (RoomListingId is null)");
            throw new InvalidOperationException("Transaction is not a room-listing payment.");
        }

        if (transaction.Status == "SUCCESS")
        {
            _logger.LogWarning($"Transaction {transaction.Id} already processed");
            throw new InvalidOperationException("Transaction already processed.");
        }

        // Only the client-facing path rejects an already-FAILED transaction outright (guards
        // against a client resubmitting a stale/bad signature). The webhook path
        // (skipSignatureCheck: true) deliberately lets this through — Razorpay's own docs
        // describe genuine "late authorisations after apparent failures" (esp. UPI), so a
        // FAILED transaction can still receive a real, signature-verified payment.captured
        // later, and that must still activate rather than being silently discarded here.
        if (transaction.Status == "FAILED" && !skipSignatureCheck)
        {
            _logger.LogWarning($"Transaction {transaction.Id} previously failed: {transaction.FailureReason}");
            throw new InvalidOperationException("Transaction previously failed. Please start a new payment.");
        }

        // Look up plan to determine routing (originalPrice-based, not name-based)
        var plan = await _unitOfWork.RoomPlans.GetByPlanTypeAsync(transaction.PlanType);
        bool isFree = plan?.OriginalPrice == 0;

        // Verify Razorpay signature for paid plans only — skipped when called from the webhook
        // receiver, which already authenticated the request via its own (different) signature.
        if (!isFree && !skipSignatureCheck)
        {
            if (!_razorpay.VerifyPaymentSignature(request.RazorpayOrderId, request.RazorpayPaymentId, request.RazorpaySignature))
            {
                _logger.LogWarning($"Signature verification failed for transaction {transaction.Id}");
                transaction.Status = "FAILED";
                transaction.FailureReason = "Signature verification failed";
                await _unitOfWork.SaveChangesAsync();
                throw new InvalidOperationException("Payment verification failed.");
            }

            _logger.LogInformation($"Signature verified for transaction {transaction.Id}");
        }

        try
        {
            transaction.Status = "SUCCESS";
            transaction.RazorpayPaymentId = request.RazorpayPaymentId;
            transaction.RazorpaySignature = request.RazorpaySignature;
            transaction.CompletedAt = DateTime.UtcNow;

            var paymentFeature = await _unitOfWork.Features.GetByKeyAsync(FeatureKeys.RoomPayment);
            if (paymentFeature == null)
                throw new InvalidOperationException("Payment feature not configured.");

            if (plan == null)
            {
                _logger.LogError($"RoomPlan '{transaction.PlanType}' not found");
                throw new InvalidOperationException("RoomPlan configuration not found.");
            }

            // Extend from whatever's currently active (if still valid) rather than resetting to
            // "now" — a renewal before expiry adds to the remaining time instead of discarding it.
            var previousMembership = await DeactivateExistingMembershipsAsync(userId);
            var baseline = (previousMembership != null && previousMembership.ValidUntil > DateTime.UtcNow)
                ? previousMembership.ValidUntil
                : DateTime.UtcNow;

            var membership = new RoomMembership
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                PlanType = transaction.PlanType,
                ValidFrom = DateTime.UtcNow,
                ValidUntil = baseline.AddDays(plan.Days),
                MaxRooms = plan.RoomLimit,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            await _unitOfWork.RoomMemberships.AddAsync(membership);

            var roomDistrictIds = new HashSet<Guid?>();
            if (transaction.RoomListingId.HasValue)
            {
                var listing = await _unitOfWork.RoomListings.GetByIdAsync(transaction.RoomListingId.Value);
                if (listing != null && !listing.IsDeleted)
                {
                    // Always (re)apply IsActive/ValidUntil from this payment's membership — never skip
                    // based on the listing's current IsActive flag. That flag is only flushed to false
                    // once a day by MembershipExpiryService, so a listing whose membership already
                    // expired hours ago can still read IsActive=true here; skipping the update in that
                    // window silently discarded the plan the user just paid for.
                    // IsDeleted guard: the user may have deleted this listing after starting the
                    // payment but before Razorpay's checkout completed — don't resurrect it.
                    listing.IsActive = true;
                    listing.ValidUntil = membership.ValidUntil;
                    roomDistrictIds.Add(listing.DistrictId);
                    _logger.LogInformation($"RoomListing {listing.Id} activated with membership valid until {membership.ValidUntil}");
                }
            }

            // Extend ValidUntil on all other existing active listings to match the new membership
            var allListings = await _unitOfWork.RoomListings.GetByUserIdAsync(userId);
            foreach (var l in allListings.Where(l => l.IsActive && !l.IsDeleted && l.Id != transaction.RoomListingId))
            {
                l.ValidUntil = membership.ValidUntil;
                roomDistrictIds.Add(l.DistrictId);
                _logger.LogInformation($"Existing listing {l.Id} ValidUntil extended to {membership.ValidUntil}");
            }

            var user = await _unitOfWork.Users.GetByIdAsync(userId);
            if (user == null)
            {
                _logger.LogError($"User {userId} not found during payment verification");
                throw new KeyNotFoundException("User not found.");
            }

            // Mark as used free plan whenever any plan is activated (free or paid)
            if (paymentFeature.IsEnabled && !user.HasUsedFreePlan)
            {
                user.HasUsedFreePlan = true;
                _logger.LogInformation($"User {userId} marked as used free plan");
            }

            await _unitOfWork.SaveChangesAsync();
            foreach (var districtId in roomDistrictIds)
                await InvalidateNearbyCacheAsync(districtId);

            _logger.LogInformation($"Payment verified and activated: transaction {transaction.Id}, membership {membership.Id}");

            return new PaymentVerifyResponse
            {
                Success = true,
                Message = "Payment successful. Your listing is now live!",
                RoomMembershipId = membership.Id,
                ValidUntil = membership.ValidUntil,
                PlanType = transaction.PlanType,
                MaxRooms = membership.MaxRooms
            };
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error during payment verification: {ex.Message}", ex);
            throw;
        }
    }

    public async Task<bool> CanUserActivateListingAsync(Guid userId)
    {
        var membership = await _unitOfWork.RoomMemberships.GetActiveByUserIdAsync(userId);
        if (membership == null)
        {
            _logger.LogInformation($"User {userId} has no active membership - can activate a listing");
            return true;
        }

        var activeRooms = await GetActiveRoomCountAsync(userId);
        var canActivate = activeRooms < membership.MaxRooms;

        _logger.LogInformation($"User {userId} has {activeRooms} active rooms, max allowed: {membership.MaxRooms}, can activate: {canActivate}");
        return canActivate;
    }

    public async Task<int> GetActiveRoomCountAsync(Guid userId)
    {
        var listings = await _unitOfWork.RoomListings.GetByUserIdAsync(userId);
        var activeCount = listings.Count(l => l.IsActive && !l.IsDeleted);
        return activeCount;
    }

    // Activates any plan with price == 0 — name is irrelevant, routing is by price
    private async Task ActivateZeroPricePlanAsync(Guid userId, Guid transactionId)
    {
        var transaction = await _unitOfWork.PaymentTransactions.GetByIdAsync(transactionId);
        if (transaction == null)
            throw new KeyNotFoundException("Transaction not found.");

        var plan = await _unitOfWork.RoomPlans.GetByPlanTypeAsync(transaction.PlanType);
        if (plan == null)
            throw new InvalidOperationException($"RoomPlan '{transaction.PlanType}' not configured.");

        transaction.Status = "SUCCESS";
        transaction.CompletedAt = DateTime.UtcNow;

        var previousMembership = await DeactivateExistingMembershipsAsync(userId);
        var baseline = (previousMembership != null && previousMembership.ValidUntil > DateTime.UtcNow)
            ? previousMembership.ValidUntil
            : DateTime.UtcNow;

        var membership = new RoomMembership
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            PlanType = transaction.PlanType,
            ValidFrom = DateTime.UtcNow,
            ValidUntil = baseline.AddDays(plan.Days),
            MaxRooms = plan.RoomLimit,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await _unitOfWork.RoomMemberships.AddAsync(membership);

        Guid? districtId = null;
        if (transaction.RoomListingId.HasValue)
        {
            var listing = await _unitOfWork.RoomListings.GetByIdAsync(transaction.RoomListingId.Value);
            if (listing != null && !listing.IsDeleted)
            {
                // Always (re)apply — see the matching comment in VerifyAndActivateAsync for why
                // skipping this on listing.IsActive==true is wrong (stale flag, only flushed daily).
                // IsDeleted guard: don't resurrect a listing deleted mid-payment.
                listing.IsActive = true;
                listing.ValidUntil = membership.ValidUntil;
                districtId = listing.DistrictId;
                _logger.LogInformation($"RoomListing {listing.Id} activated with {transaction.PlanType} plan valid until {membership.ValidUntil}");
            }
        }

        var user = await _unitOfWork.Users.GetByIdAsync(userId);
        if (user != null)
        {
            // Mark as used free plan for any price=0 plan (when payment feature is enabled)
            var paymentFeature = await _unitOfWork.Features.GetByKeyAsync(FeatureKeys.RoomPayment);
            if (paymentFeature?.IsEnabled == true && !user.HasUsedFreePlan)
            {
                user.HasUsedFreePlan = true;
                _logger.LogInformation($"User {userId} marked as used free plan");
            }
        }

        await _unitOfWork.SaveChangesAsync();
        await InvalidateNearbyCacheAsync(districtId);
    }

    public async Task<CreatePaymentOrderResponse> CreateUpgradeOrderAsync(Guid userId, string planType)
    {
        var paymentFeature = await _unitOfWork.Features.GetByKeyAsync(FeatureKeys.RoomPayment);
        if (paymentFeature == null || !paymentFeature.IsEnabled)
            throw new InvalidOperationException("Payment feature is not enabled.");

        var plan = await _unitOfWork.RoomPlans.GetByPlanTypeAsync(planType);
        if (plan == null || !plan.IsEnabled)
            throw new ArgumentException($"RoomPlan '{planType}' does not exist or is disabled.");
        if (plan.OriginalPrice == 0)
            throw new ArgumentException("Upgrade requires a paid plan (price must be greater than 0).");

        // TransactionKind == null (not "PLOT") is required here: room and plot plans share the
        // same PlanType strings (e.g. both seed a "STANDARD"), and a plot-upgrade transaction
        // also has RoomListingId == null — without this filter, a pending plot-upgrade order
        // could be mistaken for a room-upgrade one and get verified as the wrong kind entirely.
        var existing = (await _unitOfWork.PaymentTransactions.GetByUserIdAsync(userId))
            .FirstOrDefault(t => t.RoomListingId == null && t.PlotId == null && t.TransactionKind == null && t.PlanType == planType && t.Status == "PENDING");
        if (existing != null && !string.IsNullOrEmpty(existing.RazorpayOrderId))
        {
            if (existing.CreatedAt > DateTime.UtcNow.AddMinutes(-20))
            {
                return new CreatePaymentOrderResponse
                {
                    OrderId = existing.RazorpayOrderId!,
                    Amount = plan.OriginalPrice,
                    Currency = "INR",
                    KeyId = _razorpay.GetKeyId()
                };
            }
            // ABANDONED not FAILED — see the matching comment in CreateOrderAsync for why a
            // timeout presumption must stay recoverable if the original payment completes late.
            existing.Status = "ABANDONED";
            existing.FailureReason = "Razorpay order expired — superseded by retry";
            await _unitOfWork.SaveChangesAsync();
        }

        var upgradeUser = await _unitOfWork.Users.GetByIdAsync(userId);
        var (orderId, _) = await _razorpay.CreateOrderAsync(plan.OriginalPrice, Guid.NewGuid().ToString());
        var transaction = new PaymentTransaction
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            PhoneNumber = upgradeUser?.PhoneNumber ?? string.Empty,
            RoomListingId = null,
            PlanType = planType,
            Amount = plan.OriginalPrice,
            Status = "PENDING",
            RazorpayOrderId = orderId,
            CreatedAt = DateTime.UtcNow
        };
        await _unitOfWork.PaymentTransactions.AddAsync(transaction);
        try
        {
            await _unitOfWork.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Lost a race against a concurrent upgrade-order request for this same user+plan —
            // the partial unique index on PENDING room-upgrade transactions caught it.
            var winner = (await _unitOfWork.PaymentTransactions.GetByUserIdAsync(userId))
                .FirstOrDefault(t => t.RoomListingId == null && t.PlotId == null && t.TransactionKind == null && t.PlanType == planType && t.Status == "PENDING");
            if (winner != null && !string.IsNullOrEmpty(winner.RazorpayOrderId))
            {
                return new CreatePaymentOrderResponse
                {
                    OrderId = winner.RazorpayOrderId!,
                    Amount = plan.OriginalPrice,
                    Currency = "INR",
                    KeyId = _razorpay.GetKeyId()
                };
            }
            throw new InvalidOperationException("Upgrade order already exists. Please try again.");
        }

        return new CreatePaymentOrderResponse
        {
            OrderId = orderId,
            Amount = plan.OriginalPrice,
            Currency = "INR",
            KeyId = _razorpay.GetKeyId()
        };
    }

    public async Task<PaymentVerifyResponse> VerifyUpgradePaymentAsync(Guid userId, VerifyPaymentRequest request, bool skipSignatureCheck = false)
    {
        var transaction = await _unitOfWork.PaymentTransactions.GetByRazorpayOrderIdAsync(request.RazorpayOrderId);
        if (transaction == null) throw new KeyNotFoundException("Transaction not found.");
        if (transaction.UserId != userId) throw new UnauthorizedAccessException("Not your transaction.");
        // Defense in depth: room and plot upgrade transactions can share the same PlanType string
        // (both seed e.g. "STANDARD"), and a plot-upgrade transaction also has RoomListingId ==
        // null — CreateUpgradeOrderAsync's own lookups are now kind-filtered to prevent handing
        // out a plot transaction's order here in the first place, but this guard makes sure a
        // wrong-kind transaction can never activate a RoomMembership even if it somehow arrives.
        // Also rejects RoomListingId != null: a genuine room-FRESH-purchase transaction's
        // credentials submitted here would otherwise pass (same PlanType, no PlotId/PLOT kind),
        // get marked SUCCESS without ever activating its actual listing, and permanently block
        // the correct VerifyAndActivateAsync call afterward ("already processed").
        if (transaction.TransactionKind == "PLOT" || transaction.PlotId != null || transaction.RoomListingId != null)
            throw new InvalidOperationException("Transaction is not a room-plan upgrade.");
        if (transaction.Status == "SUCCESS") throw new InvalidOperationException("Already processed.");

        if (!skipSignatureCheck && !_razorpay.VerifyPaymentSignature(request.RazorpayOrderId, request.RazorpayPaymentId, request.RazorpaySignature))
        {
            transaction.Status = "FAILED";
            transaction.FailureReason = "Signature verification failed";
            await _unitOfWork.SaveChangesAsync();
            throw new InvalidOperationException("Payment verification failed.");
        }

        transaction.Status = "SUCCESS";
        transaction.RazorpayPaymentId = request.RazorpayPaymentId;
        transaction.RazorpaySignature = request.RazorpaySignature;
        transaction.CompletedAt = DateTime.UtcNow;

        // Use the plan stored on the transaction — supports any paid plan type
        var plan = await _unitOfWork.RoomPlans.GetByPlanTypeAsync(transaction.PlanType);
        if (plan == null)
            throw new InvalidOperationException($"RoomPlan '{transaction.PlanType}' not configured.");

        var previousMembership = await DeactivateExistingMembershipsAsync(userId);
        var baseline = (previousMembership != null && previousMembership.ValidUntil > DateTime.UtcNow)
            ? previousMembership.ValidUntil
            : DateTime.UtcNow;

        var membership = new RoomMembership
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            PlanType = transaction.PlanType,
            ValidFrom = DateTime.UtcNow,
            ValidUntil = baseline.AddDays(plan.Days),
            MaxRooms = plan.RoomLimit,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await _unitOfWork.RoomMemberships.AddAsync(membership);

        var listings = await _unitOfWork.RoomListings.GetByUserIdAsync(userId);
        var districtIds = new HashSet<Guid?>();
        foreach (var listing in listings.Where(l => l.IsActive && !l.IsDeleted))
        {
            listing.ValidUntil = membership.ValidUntil;
            districtIds.Add(listing.DistrictId);
        }

        await _unitOfWork.SaveChangesAsync();
        foreach (var districtId in districtIds)
            await InvalidateNearbyCacheAsync(districtId);

        _logger.LogInformation($"RoomPlan upgrade verified for user {userId}, membership {membership.Id}");
        return new PaymentVerifyResponse
        {
            Success = true,
            Message = $"RoomPlan upgraded! Your existing rooms are extended to {plan.Days} days.",
            RoomMembershipId = membership.Id,
            ValidUntil = membership.ValidUntil,
            PlanType = transaction.PlanType,
            MaxRooms = membership.MaxRooms
        };
    }

    // ── PlotListing payment methods ──────────────────────────────────────────────────

    // See the matching comment on DeactivateExistingMembershipsAsync (room side) for why this
    // returns the deactivated membership instead of discarding it.
    private async Task<PlotMembership?> DeactivateExistingPlotMembershipsAsync(Guid userId)
    {
        var existing = await _unitOfWork.PlotMemberships.GetActiveByUserIdAsync(userId);
        if (existing != null)
        {
            existing.IsActive = false;
            existing.UpdatedAt = DateTime.UtcNow;
        }
        return existing;
    }

    public async Task<CreatePaymentOrderResponse> CreatePlotListingOrderAsync(Guid userId, Guid plotId, string planType)
    {
        var plan = await _unitOfWork.PlotPlans.GetByPlanTypeAsync(planType);
        if (plan == null || !plan.IsEnabled)
            throw new ArgumentException($"PlotListing plan '{planType}' does not exist or is disabled.");
        bool isFree = plan.OriginalPrice == 0;

        var plotPaymentFeature = await _unitOfWork.Features.GetByKeyAsync(FeatureKeys.PlotListingPayment);
        if (plotPaymentFeature == null)
            throw new InvalidOperationException("PlotListing payment feature not configured.");

        if (!plotPaymentFeature.IsEnabled && !isFree)
            throw new InvalidOperationException("PlotListing payment feature is not enabled yet.");

        var plot = await _unitOfWork.PlotListings.GetByIdAsync(plotId);
        if (plot == null)
            throw new KeyNotFoundException("PlotListing not found.");
        if (plot.UserId != userId)
            throw new UnauthorizedAccessException("You don't own this plot.");

        _logger.LogInformation($"Creating plot order for {planType} plan, user {userId}, plot {plotId}");

        var existingPending = (await _unitOfWork.PaymentTransactions.GetByUserIdAsync(userId))
            .FirstOrDefault(t => t.PlotId == plotId && t.Status == "PENDING");

        if (existingPending != null)
        {
            if (isFree)
            {
                await ActivateZeroPricePlotPlanAsync(userId, existingPending.Id);
                return new CreatePaymentOrderResponse { OrderId = existingPending.Id.ToString(), Amount = 0, Currency = "INR", KeyId = string.Empty };
            }
            else
            {
                if (!string.IsNullOrEmpty(existingPending.RazorpayOrderId))
                {
                    if (existingPending.CreatedAt > DateTime.UtcNow.AddMinutes(-20))
                        return new CreatePaymentOrderResponse { OrderId = existingPending.RazorpayOrderId!, Amount = plan.OriginalPrice, Currency = "INR", KeyId = _razorpay.GetKeyId() };
                    // ABANDONED not FAILED — see the matching comment in CreateOrderAsync.
                    existingPending.Status = "ABANDONED";
                    existingPending.FailureReason = "Razorpay order expired — superseded by retry";
                    await _unitOfWork.SaveChangesAsync();
                }
            }
        }

        var plotTxUser = await _unitOfWork.Users.GetByIdAsync(userId);

        if (isFree && plotPaymentFeature.IsEnabled)
        {
            if (plotTxUser?.HasUsedFreePlotPlan == true)
            {
                _logger.LogWarning($"User {userId} attempted to reuse free plot plan");
                throw new InvalidOperationException("You have already used the free plot plan. Please use the paid plan.");
            }
        }

        var transaction = new PaymentTransaction
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            PhoneNumber = plotTxUser?.PhoneNumber ?? string.Empty,
            PlotId = plotId,
            TransactionKind = "PLOT",
            PlanType = planType,
            Amount = plan.OriginalPrice,
            Status = "PENDING",
            CreatedAt = DateTime.UtcNow
        };

        if (!isFree)
        {
            var (orderId, returnedAmount) = await _razorpay.CreateOrderAsync(plan.OriginalPrice, transaction.Id.ToString());
            if (returnedAmount != plan.OriginalPrice)
                throw new InvalidOperationException("Payment amount mismatch. Please try again.");
            transaction.RazorpayOrderId = orderId;
        }

        await _unitOfWork.PaymentTransactions.AddAsync(transaction);
        try
        {
            await _unitOfWork.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Lost a race against a concurrent create-order call for this same plot — the
            // partial unique index on PENDING plot-listing transactions caught it.
            var winner = (await _unitOfWork.PaymentTransactions.GetByUserIdAsync(userId))
                .FirstOrDefault(t => t.PlotId == plotId && t.Status == "PENDING");
            if (winner != null && !isFree && !string.IsNullOrEmpty(winner.RazorpayOrderId))
            {
                return new CreatePaymentOrderResponse { OrderId = winner.RazorpayOrderId!, Amount = plan.OriginalPrice, Currency = "INR", KeyId = _razorpay.GetKeyId() };
            }
            throw new InvalidOperationException("Payment order already exists for this plot. Please try again.");
        }

        if (isFree)
        {
            await ActivateZeroPricePlotPlanAsync(userId, transaction.Id);
            return new CreatePaymentOrderResponse { OrderId = transaction.Id.ToString(), Amount = 0, Currency = "INR", KeyId = string.Empty };
        }

        return new CreatePaymentOrderResponse { OrderId = transaction.RazorpayOrderId!, Amount = plan.OriginalPrice, Currency = "INR", KeyId = _razorpay.GetKeyId() };
    }

    public async Task<PlotListingPaymentVerifyResponse> VerifyPlotListingPaymentAsync(Guid userId, VerifyPaymentRequest request, bool skipSignatureCheck = false)
    {
        _logger.LogInformation($"Verifying plot payment for user {userId}, order {request.RazorpayOrderId}");

        var transaction = await _unitOfWork.PaymentTransactions.GetByRazorpayOrderIdAsync(request.RazorpayOrderId);
        if (transaction == null) throw new KeyNotFoundException("Transaction not found.");
        if (transaction.UserId != userId) throw new UnauthorizedAccessException("Transaction doesn't belong to you.");
        // See the matching comment in VerifyAndActivateAsync — only a genuine plot-fresh-purchase
        // transaction ever has PlotId set (plot upgrades and any room transaction never set it).
        if (transaction.PlotId == null) throw new InvalidOperationException("Transaction is not a plot-listing payment.");
        if (transaction.Status == "SUCCESS") throw new InvalidOperationException("Transaction already processed.");
        // See the matching comment in VerifyAndActivateAsync — the webhook path (skipSignatureCheck)
        // deliberately lets a late genuine payment.captured through even for a FAILED transaction.
        if (transaction.Status == "FAILED" && !skipSignatureCheck) throw new InvalidOperationException("Transaction previously failed. Please start a new payment.");

        var plan = await _unitOfWork.PlotPlans.GetByPlanTypeAsync(transaction.PlanType);
        bool isFree = plan?.OriginalPrice == 0;

        if (!isFree && !skipSignatureCheck)
        {
            if (!_razorpay.VerifyPaymentSignature(request.RazorpayOrderId, request.RazorpayPaymentId, request.RazorpaySignature))
            {
                transaction.Status = "FAILED";
                transaction.FailureReason = "Signature verification failed";
                await _unitOfWork.SaveChangesAsync();
                throw new InvalidOperationException("Payment verification failed.");
            }
        }

        transaction.Status = "SUCCESS";
        transaction.RazorpayPaymentId = request.RazorpayPaymentId;
        transaction.RazorpaySignature = request.RazorpaySignature;
        transaction.CompletedAt = DateTime.UtcNow;

        var plotPaymentFeature = await _unitOfWork.Features.GetByKeyAsync(FeatureKeys.PlotListingPayment);
        if (plan == null) throw new InvalidOperationException("RoomPlan configuration not found.");

        var previousMembership = await DeactivateExistingPlotMembershipsAsync(userId);
        var baseline = (previousMembership != null && previousMembership.ValidUntil > DateTime.UtcNow)
            ? previousMembership.ValidUntil
            : DateTime.UtcNow;

        var membership = new PlotMembership
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            PlanType = transaction.PlanType,
            ValidFrom = DateTime.UtcNow,
            ValidUntil = baseline.AddDays(plan.Days),
            MaxPlotListings = plan.PlotListingLimit,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await _unitOfWork.PlotMemberships.AddAsync(membership);

        var plotDistrictIds = new HashSet<Guid?>();
        if (transaction.PlotId.HasValue)
        {
            var plot = await _unitOfWork.PlotListings.GetByIdAsync(transaction.PlotId.Value);
            if (plot != null && !plot.IsDeleted)
            {
                // Always (re)apply IsActive/ValidUntil from this payment's membership — see the
                // matching comment in VerifyAndActivateAsync for why skipping on IsActive==true is wrong.
                // IsDeleted guard: don't resurrect a plot deleted mid-payment.
                plot.IsActive = true;
                plot.ValidUntil = membership.ValidUntil;
                plotDistrictIds.Add(plot.DistrictId);
                _logger.LogInformation($"PlotListing {plot.Id} activated with membership valid until {membership.ValidUntil}");
            }
        }

        // Extend ValidUntil on all other existing active plots to match the new membership
        var allPlotListings = await _unitOfWork.PlotListings.GetActiveByUserIdAsync(userId);
        foreach (var p in allPlotListings.Where(p => !p.IsDeleted && p.Id != transaction.PlotId))
        {
            p.ValidUntil = membership.ValidUntil;
            plotDistrictIds.Add(p.DistrictId);
            _logger.LogInformation($"Existing plot {p.Id} ValidUntil extended to {membership.ValidUntil}");
        }

        var user = await _unitOfWork.Users.GetByIdAsync(userId);
        if (user == null) throw new KeyNotFoundException("User not found.");

        if (plotPaymentFeature?.IsEnabled == true && !user.HasUsedFreePlotPlan)
        {
            user.HasUsedFreePlotPlan = true;
        }

        await _unitOfWork.SaveChangesAsync();
        foreach (var districtId in plotDistrictIds)
            await InvalidatePlotListingNearbyCacheAsync(districtId);

        return new PlotListingPaymentVerifyResponse
        {
            Success = true,
            Message = "Payment successful. Your plot listing is now live!",
            PlotMembershipId = membership.Id,
            ValidUntil = membership.ValidUntil,
            PlanType = transaction.PlanType,
            MaxPlotListings = membership.MaxPlotListings
        };
    }

    public async Task<bool> CanUserActivatePlotListingAsync(Guid userId)
    {
        var membership = await _unitOfWork.PlotMemberships.GetActiveByUserIdAsync(userId);
        if (membership == null) return true;

        var activePlotListings = await _unitOfWork.PlotListings.GetActiveByUserIdAsync(userId);
        var activeCount = activePlotListings.Count(p => !p.IsDeleted);
        var canActivate = activeCount < membership.MaxPlotListings;

        _logger.LogInformation($"User {userId} has {activeCount} active plots, max allowed: {membership.MaxPlotListings}, can activate: {canActivate}");
        return canActivate;
    }

    public async Task<int> GetActivePlotListingCountAsync(Guid userId)
    {
        var plots = await _unitOfWork.PlotListings.GetActiveByUserIdAsync(userId);
        return plots.Count(p => !p.IsDeleted);
    }

    public async Task<CreatePaymentOrderResponse> CreatePlotListingUpgradeOrderAsync(Guid userId, string planType)
    {
        var plotPaymentFeature = await _unitOfWork.Features.GetByKeyAsync(FeatureKeys.PlotListingPayment);
        if (plotPaymentFeature == null || !plotPaymentFeature.IsEnabled)
            throw new InvalidOperationException("PlotListing payment feature is not enabled.");

        var plan = await _unitOfWork.PlotPlans.GetByPlanTypeAsync(planType);
        if (plan == null || !plan.IsEnabled)
            throw new ArgumentException($"PlotListing plan '{planType}' does not exist or is disabled.");
        if (plan.OriginalPrice == 0)
            throw new ArgumentException("Upgrade requires a paid plan.");

        var existing = (await _unitOfWork.PaymentTransactions.GetByUserIdAsync(userId))
            .FirstOrDefault(t => t.PlotId == null && t.RoomListingId == null && t.TransactionKind == "PLOT" && t.PlanType == planType && t.Status == "PENDING");
        if (existing != null && !string.IsNullOrEmpty(existing.RazorpayOrderId))
        {
            if (existing.CreatedAt > DateTime.UtcNow.AddMinutes(-20))
                return new CreatePaymentOrderResponse { OrderId = existing.RazorpayOrderId!, Amount = plan.OriginalPrice, Currency = "INR", KeyId = _razorpay.GetKeyId() };
            // ABANDONED not FAILED — see the matching comment in CreateOrderAsync.
            existing.Status = "ABANDONED";
            existing.FailureReason = "Razorpay order expired — superseded by retry";
            await _unitOfWork.SaveChangesAsync();
        }

        var plotUpgradeUser = await _unitOfWork.Users.GetByIdAsync(userId);
        var (orderId, _) = await _razorpay.CreateOrderAsync(plan.OriginalPrice, Guid.NewGuid().ToString());
        var transaction = new PaymentTransaction
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            PhoneNumber = plotUpgradeUser?.PhoneNumber ?? string.Empty,
            PlotId = null,
            RoomListingId = null,
            TransactionKind = "PLOT",
            PlanType = planType,
            Amount = plan.OriginalPrice,
            Status = "PENDING",
            RazorpayOrderId = orderId,
            CreatedAt = DateTime.UtcNow
        };
        await _unitOfWork.PaymentTransactions.AddAsync(transaction);
        try
        {
            await _unitOfWork.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Lost a race against a concurrent plot-upgrade request for this same user+plan —
            // the partial unique index on PENDING plot-upgrade transactions caught it.
            var winner = (await _unitOfWork.PaymentTransactions.GetByUserIdAsync(userId))
                .FirstOrDefault(t => t.PlotId == null && t.RoomListingId == null && t.TransactionKind == "PLOT" && t.PlanType == planType && t.Status == "PENDING");
            if (winner != null && !string.IsNullOrEmpty(winner.RazorpayOrderId))
            {
                return new CreatePaymentOrderResponse { OrderId = winner.RazorpayOrderId!, Amount = plan.OriginalPrice, Currency = "INR", KeyId = _razorpay.GetKeyId() };
            }
            throw new InvalidOperationException("Upgrade order already exists. Please try again.");
        }

        return new CreatePaymentOrderResponse { OrderId = orderId, Amount = plan.OriginalPrice, Currency = "INR", KeyId = _razorpay.GetKeyId() };
    }

    public async Task<PlotListingPaymentVerifyResponse> VerifyPlotListingUpgradePaymentAsync(Guid userId, VerifyPaymentRequest request, bool skipSignatureCheck = false)
    {
        var transaction = await _unitOfWork.PaymentTransactions.GetByRazorpayOrderIdAsync(request.RazorpayOrderId);
        if (transaction == null) throw new KeyNotFoundException("Transaction not found.");
        if (transaction.UserId != userId) throw new UnauthorizedAccessException("Not your transaction.");
        // Defense in depth (mirrors the guard in VerifyUpgradePaymentAsync): this transaction's
        // own creation lookup (CreatePlotListingUpgradeOrderAsync) already filters by
        // TransactionKind == "PLOT", so a room transaction should never reach here through
        // normal flow — but reject explicitly rather than silently trusting that invariant.
        // Also rejects PlotId != null: a genuine plot-FRESH-purchase transaction's credentials
        // submitted here would otherwise pass (same PlanType, no RoomListingId, already "PLOT"
        // kind), get marked SUCCESS without ever activating its actual plot, and permanently
        // block the correct VerifyPlotListingPaymentAsync call afterward ("already processed").
        if (transaction.TransactionKind != "PLOT" || transaction.RoomListingId != null || transaction.PlotId != null)
            throw new InvalidOperationException("Transaction is not a plot-plan upgrade.");
        if (transaction.Status == "SUCCESS") throw new InvalidOperationException("Already processed.");

        if (!skipSignatureCheck && !_razorpay.VerifyPaymentSignature(request.RazorpayOrderId, request.RazorpayPaymentId, request.RazorpaySignature))
        {
            transaction.Status = "FAILED";
            transaction.FailureReason = "Signature verification failed";
            await _unitOfWork.SaveChangesAsync();
            throw new InvalidOperationException("Payment verification failed.");
        }

        transaction.Status = "SUCCESS";
        transaction.RazorpayPaymentId = request.RazorpayPaymentId;
        transaction.RazorpaySignature = request.RazorpaySignature;
        transaction.CompletedAt = DateTime.UtcNow;

        var plan = await _unitOfWork.PlotPlans.GetByPlanTypeAsync(transaction.PlanType);
        if (plan == null) throw new InvalidOperationException($"RoomPlan '{transaction.PlanType}' not configured.");

        var previousMembership = await DeactivateExistingPlotMembershipsAsync(userId);
        var baseline = (previousMembership != null && previousMembership.ValidUntil > DateTime.UtcNow)
            ? previousMembership.ValidUntil
            : DateTime.UtcNow;

        var membership = new PlotMembership
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            PlanType = transaction.PlanType,
            ValidFrom = DateTime.UtcNow,
            ValidUntil = baseline.AddDays(plan.Days),
            MaxPlotListings = plan.PlotListingLimit,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await _unitOfWork.PlotMemberships.AddAsync(membership);

        var activePlotListings = await _unitOfWork.PlotListings.GetActiveByUserIdAsync(userId);
        foreach (var plot in activePlotListings)
            plot.ValidUntil = membership.ValidUntil;

        await _unitOfWork.SaveChangesAsync();

        var districtIds = activePlotListings.Select(p => p.DistrictId).Distinct();
        foreach (var did in districtIds)
            await InvalidatePlotListingNearbyCacheAsync(did);

        _logger.LogInformation($"PlotListing plan upgrade verified for user {userId}, membership {membership.Id}");
        return new PlotListingPaymentVerifyResponse
        {
            Success = true,
            Message = $"PlotListing plan upgraded! Your existing plots are extended to {plan.Days} days.",
            PlotMembershipId = membership.Id,
            ValidUntil = membership.ValidUntil,
            PlanType = transaction.PlanType,
            MaxPlotListings = membership.MaxPlotListings
        };
    }

    private async Task ActivateZeroPricePlotPlanAsync(Guid userId, Guid transactionId)
    {
        var transaction = await _unitOfWork.PaymentTransactions.GetByIdAsync(transactionId);
        if (transaction == null) throw new KeyNotFoundException("Transaction not found.");

        var plan = await _unitOfWork.PlotPlans.GetByPlanTypeAsync(transaction.PlanType);
        if (plan == null) throw new InvalidOperationException($"PlotListing plan '{transaction.PlanType}' not configured.");

        transaction.Status = "SUCCESS";
        transaction.CompletedAt = DateTime.UtcNow;

        var previousMembership = await DeactivateExistingPlotMembershipsAsync(userId);
        var baseline = (previousMembership != null && previousMembership.ValidUntil > DateTime.UtcNow)
            ? previousMembership.ValidUntil
            : DateTime.UtcNow;

        var membership = new PlotMembership
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            PlanType = transaction.PlanType,
            ValidFrom = DateTime.UtcNow,
            ValidUntil = baseline.AddDays(plan.Days),
            MaxPlotListings = plan.PlotListingLimit,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await _unitOfWork.PlotMemberships.AddAsync(membership);

        Guid? freePlotListingDistrictId = null;
        if (transaction.PlotId.HasValue)
        {
            var plot = await _unitOfWork.PlotListings.GetByIdAsync(transaction.PlotId.Value);
            if (plot != null && !plot.IsDeleted)
            {
                // Always (re)apply — see the matching comment in VerifyAndActivateAsync for why
                // skipping this on plot.IsActive==true is wrong (stale flag, only flushed daily).
                // IsDeleted guard: don't resurrect a plot deleted mid-payment.
                plot.IsActive = true;
                plot.ValidUntil = membership.ValidUntil;
                freePlotListingDistrictId = plot.DistrictId;
            }
        }

        var user = await _unitOfWork.Users.GetByIdAsync(userId);
        if (user != null)
        {
            var plotPaymentFeature = await _unitOfWork.Features.GetByKeyAsync(FeatureKeys.PlotListingPayment);
            if (plotPaymentFeature?.IsEnabled == true && !user.HasUsedFreePlotPlan)
            {
                user.HasUsedFreePlotPlan = true;
            }
        }

        await _unitOfWork.SaveChangesAsync();
        await InvalidatePlotListingNearbyCacheAsync(freePlotListingDistrictId);
    }
}
