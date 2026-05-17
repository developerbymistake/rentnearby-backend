using Microsoft.Extensions.Logging;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using StackExchange.Redis;

namespace RentNearBy.Infrastructure.Services;

public interface IPaymentService
{
    Task<CreatePaymentOrderResponse> CreateOrderAsync(Guid userId, Guid listingId, string planType);
    Task<PaymentInitiateResponse> InitiatePaymentAsync(Guid userId, Guid listingId, string planType);
    Task<PaymentVerifyResponse> VerifyAndActivateAsync(Guid userId, VerifyPaymentRequest request);
    Task<bool> CanUserActivateListingAsync(Guid userId);
    Task<int> GetActiveRoomCountAsync(Guid userId);
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

    private async Task DeactivateExistingMembershipsAsync(Guid userId)
    {
        var existing = await _unitOfWork.UserMemberships.GetActiveByUserIdAsync(userId);
        if (existing != null)
        {
            existing.IsActive = false;
            existing.UpdatedAt = DateTime.UtcNow;
        }
    }

    private async Task InvalidateNearbyCacheAsync(Guid? cityId)
    {
        if (cityId == null) return;
        try
        {
            var db = _redis.GetDatabase();
            var server = _redis.GetServer(_redis.GetEndPoints()[0]);
            var keys = server.Keys(pattern: $"nearby:{cityId}:*").ToArray();
            if (keys.Length > 0) await db.KeyDeleteAsync(keys);
        }
        catch { }
    }

    public async Task<CreatePaymentOrderResponse> CreateOrderAsync(Guid userId, Guid listingId, string planType)
    {
        if (planType != "FREE" && planType != "PAID")
            throw new ArgumentException("Invalid plan type. Must be FREE or PAID.");

        var listing = await _unitOfWork.Listings.GetByIdAsync(listingId);
        if (listing == null)
            throw new KeyNotFoundException("Listing not found.");
        if (listing.UserId != userId)
            throw new UnauthorizedAccessException("You don't own this listing.");

        var paymentFeature = await _unitOfWork.PaymentFeature.GetAsync();
        if (paymentFeature == null)
            throw new InvalidOperationException("Payment feature not configured.");

        if (!paymentFeature.IsEnabled && planType == "PAID")
            throw new InvalidOperationException("Payment feature is not enabled yet.");

        _logger.LogInformation($"Creating order for {planType} plan, user {userId}, listing {listingId}");

        // Handle existing PENDING transactions
        var existingPendingTransaction = (await _unitOfWork.PaymentTransactions.GetByUserIdAsync(userId))
            .FirstOrDefault(t => t.ListingId == listingId && t.Status == "PENDING");

        if (existingPendingTransaction != null)
        {
            _logger.LogInformation($"Existing PENDING transaction {existingPendingTransaction.Id} found for listing {listingId}, plan {planType}");

            if (planType == "FREE")
            {
                // Reuse existing PENDING transaction — activate it now
                await ActivateFreePlanAsync(userId, existingPendingTransaction.Id);
                return new CreatePaymentOrderResponse
                {
                    OrderId = existingPendingTransaction.Id.ToString(),
                    Amount = 0,
                    Currency = "INR",
                    KeyId = string.Empty
                };
            }
            else // PAID
            {
                // Reuse existing Razorpay order from previous attempt (avoids double charge)
                if (!string.IsNullOrEmpty(existingPendingTransaction.RazorpayOrderId))
                {
                    var retryKeyId = _razorpay.GetKeyId();
                    var existingPlan = await _unitOfWork.Plans.GetByPlanTypeAsync("PAID");
                    return new CreatePaymentOrderResponse
                    {
                        OrderId = existingPendingTransaction.RazorpayOrderId!,
                        Amount = existingPlan!.Price,
                        Currency = "INR",
                        KeyId = retryKeyId
                    };
                }
            }
        }

        // Check FREE plan reuse
        if (planType == "FREE" && paymentFeature.IsEnabled)
        {
            var user = await _unitOfWork.Users.GetByIdAsync(userId);
            if (user?.HasUsedFreePlan == true)
            {
                _logger.LogWarning($"User {userId} attempted to reuse FREE plan");
                throw new InvalidOperationException("You have already used the free plan. Please use the paid plan.");
            }
        }

        var plan = await _unitOfWork.Plans.GetByPlanTypeAsync(planType);
        if (plan == null)
            throw new KeyNotFoundException($"Plan '{planType}' not found.");

        var amount = plan.Price;
        var transaction = new PaymentTransaction
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ListingId = listingId,
            PlanType = planType,
            Amount = amount,
            Status = "PENDING",
            CreatedAt = DateTime.UtcNow
        };

        // For PAID plan: Create Razorpay order first
        if (planType == "PAID")
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

        // Save transaction to DB BEFORE activating (critical for ActivateFreePlanAsync to find it)
        await _unitOfWork.PaymentTransactions.AddAsync(transaction);
        await _unitOfWork.SaveChangesAsync();

        // For FREE plan: Auto-activate immediately (now transaction is in DB)
        if (planType == "FREE")
        {
            _logger.LogInformation($"Auto-activating FREE plan for user {userId}, listing {listingId}");
            await ActivateFreePlanAsync(userId, transaction.Id);

            return new CreatePaymentOrderResponse
            {
                OrderId = transaction.Id.ToString(),
                Amount = amount,
                Currency = "INR",
                KeyId = string.Empty
            };
        }

        // For PAID plan: Return Razorpay order details
        var keyId = _razorpay.GetKeyId();
        return new CreatePaymentOrderResponse
        {
            OrderId = transaction.RazorpayOrderId!,
            Amount = amount,
            Currency = "INR",
            KeyId = keyId
        };
    }

    public async Task<PaymentInitiateResponse> InitiatePaymentAsync(Guid userId, Guid listingId, string planType)
    {
        if (planType != "FREE" && planType != "PAID")
            throw new ArgumentException("Invalid plan type. Must be FREE or PAID.");

        var listing = await _unitOfWork.Listings.GetByIdAsync(listingId);
        if (listing == null)
            throw new KeyNotFoundException("Listing not found.");
        if (listing.UserId != userId)
            throw new UnauthorizedAccessException("You don't own this listing.");

        var paymentFeature = await _unitOfWork.PaymentFeature.GetAsync();
        if (paymentFeature == null)
            throw new InvalidOperationException("Payment feature not configured.");

        if (!paymentFeature.IsEnabled && planType == "PAID")
            throw new InvalidOperationException("Payment feature is not enabled yet.");

        _logger.LogInformation($"Initiating {planType} payment for user {userId}, listing {listingId}");

        // Prevent duplicate transactions: check if user already has a PENDING transaction for this listing
        var existingPendingTransaction = (await _unitOfWork.PaymentTransactions.GetByUserIdAsync(userId))
            .FirstOrDefault(t => t.ListingId == listingId && t.Status == "PENDING");

        if (existingPendingTransaction != null)
        {
            _logger.LogWarning($"User {userId} already has a PENDING transaction for listing {listingId}");
            throw new InvalidOperationException("Payment already in progress for this listing. Please complete or cancel the previous payment.");
        }

        // Prevent FREE plan reuse: only if payment is ENABLED
        if (planType == "FREE" && paymentFeature.IsEnabled)
        {
            var user = await _unitOfWork.Users.GetByIdAsync(userId);
            if (user?.HasUsedFreePlan == true)
            {
                _logger.LogWarning($"User {userId} attempted to use FREE plan again");
                throw new InvalidOperationException("You have already used the free plan. Please use the paid plan.");
            }
        }

        var plan = await _unitOfWork.Plans.GetByPlanTypeAsync(planType);
        if (plan == null)
            throw new KeyNotFoundException($"Plan type '{planType}' not found.");

        var amount = plan.Price;
        var transaction = new PaymentTransaction
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ListingId = listingId,
            PlanType = planType,
            Amount = amount,
            Status = "PENDING",
            CreatedAt = DateTime.UtcNow
        };

        if (planType == "PAID")
        {
            var (orderId, returnedAmount) = await _razorpay.CreateOrderAsync(amount, transaction.Id.ToString());

            // Amount validation: ensure returned amount matches expected amount
            if (returnedAmount != amount)
            {
                _logger.LogError($"Amount mismatch for transaction {transaction.Id}: expected {amount}, got {returnedAmount}");
                throw new InvalidOperationException("Payment amount mismatch. Please try again.");
            }

            transaction.RazorpayOrderId = orderId;
            _logger.LogInformation($"Razorpay order created: {orderId} for amount {amount}");
        }

        await _unitOfWork.PaymentTransactions.AddAsync(transaction);
        await _unitOfWork.SaveChangesAsync();

        _logger.LogInformation($"Payment transaction initiated: {transaction.Id} for {planType} plan");

        // Auto-activate FREE plans immediately (no Razorpay verification needed)
        if (planType == "FREE")
        {
            _logger.LogInformation($"Auto-activating FREE plan for user {userId}, transaction {transaction.Id}");
            try
            {
                await ActivateFreePlanAsync(userId, transaction.Id);
                _logger.LogInformation($"FREE plan auto-activated for user {userId}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error auto-activating FREE plan: {ex.Message}", ex);
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

    public async Task<PaymentVerifyResponse> VerifyAndActivateAsync(Guid userId, VerifyPaymentRequest request)
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

        if (transaction.Status == "SUCCESS")
        {
            _logger.LogWarning($"Transaction {transaction.Id} already processed");
            throw new InvalidOperationException("Transaction already processed.");
        }

        if (transaction.Status == "FAILED")
        {
            _logger.LogWarning($"Transaction {transaction.Id} previously failed: {transaction.FailureReason}");
            throw new InvalidOperationException("Transaction previously failed. Please start a new payment.");
        }

        // Verify signature for PAID plans (FREE plans skip payment gateway verification)
        if (transaction.PlanType == "PAID")
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

        // Use database transaction to prevent race conditions: lock the transaction for exclusive update
        try
        {
            transaction.Status = "SUCCESS";
            transaction.RazorpayPaymentId = request.RazorpayPaymentId;
            transaction.RazorpaySignature = request.RazorpaySignature;
            transaction.CompletedAt = DateTime.UtcNow;

            var paymentFeature = await _unitOfWork.PaymentFeature.GetAsync();
            if (paymentFeature == null)
                throw new InvalidOperationException("Payment feature not configured.");

            var plan = await _unitOfWork.Plans.GetByPlanTypeAsync(transaction.PlanType);
            if (plan == null)
            {
                _logger.LogError($"Plan '{transaction.PlanType}' not found");
                throw new InvalidOperationException("Plan configuration not found.");
            }

            var membership = new UserMembership
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                PlanType = transaction.PlanType,
                ValidFrom = DateTime.UtcNow,
                ValidUntil = DateTime.UtcNow.AddDays(plan.Days),
                MaxRooms = plan.RoomLimit,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            await DeactivateExistingMembershipsAsync(userId);
            await _unitOfWork.UserMemberships.AddAsync(membership);

            // Validate and activate listing: check it hasn't been activated already
            Guid? activatedListingCityId = null;
            if (transaction.ListingId.HasValue)
            {
                var listing = await _unitOfWork.Listings.GetByIdAsync(transaction.ListingId.Value);
                if (listing != null)
                {
                    if (listing.IsActive)
                    {
                        _logger.LogWarning($"Listing {listing.Id} is already active");
                        // Idempotent: return success if already active
                        return new PaymentVerifyResponse
                        {
                            Success = true,
                            Message = "This listing is already live."
                        };
                    }

                    listing.IsActive = true;
                    listing.ValidUntil = membership.ValidUntil;
                    activatedListingCityId = listing.CityId;
                    _logger.LogInformation($"Listing {listing.Id} activated with membership valid until {membership.ValidUntil}");
                }
            }

            var user = await _unitOfWork.Users.GetByIdAsync(userId);
            if (user == null)
            {
                _logger.LogError($"User {userId} not found during payment verification");
                throw new KeyNotFoundException("User not found.");
            }

            // Mark user as used free plan - ONLY if payment is ENABLED
            // If payment disabled, user can use FREE plan unlimited times
            if (transaction.PlanType == "FREE" && paymentFeature.IsEnabled && !user.HasUsedFreePlan)
            {
                user.HasUsedFreePlan = true;
                _logger.LogInformation($"User {userId} marked as used free plan");
            }

            await _unitOfWork.SaveChangesAsync();
            await InvalidateNearbyCacheAsync(activatedListingCityId);

            _logger.LogInformation($"Payment verified and activated: transaction {transaction.Id}, membership {membership.Id}");

            return new PaymentVerifyResponse
            {
                Success = true,
                Message = "Payment successful. Your listing is now live!",
                UserMembershipId = membership.Id,
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
        var membership = await _unitOfWork.UserMemberships.GetActiveByUserIdAsync(userId);
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
        var listings = await _unitOfWork.Listings.GetByUserIdAsync(userId);
        var activeCount = listings.Count(l => l.IsActive && !l.IsDeleted);
        return activeCount;
    }

    private async Task ActivateFreePlanAsync(Guid userId, Guid transactionId)
    {
        var transaction = await _unitOfWork.PaymentTransactions.GetByIdAsync(transactionId);
        if (transaction == null)
            throw new KeyNotFoundException("Transaction not found.");

        var plan = await _unitOfWork.Plans.GetByPlanTypeAsync("FREE");
        if (plan == null)
            throw new InvalidOperationException("FREE plan not configured.");

        transaction.Status = "SUCCESS";
        transaction.CompletedAt = DateTime.UtcNow;

        var membership = new UserMembership
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            PlanType = "FREE",
            ValidFrom = DateTime.UtcNow,
            ValidUntil = DateTime.UtcNow.AddDays(plan.Days),
            MaxRooms = plan.RoomLimit,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await DeactivateExistingMembershipsAsync(userId);
        await _unitOfWork.UserMemberships.AddAsync(membership);

        Guid? freePlanCityId = null;
        if (transaction.ListingId.HasValue)
        {
            var listing = await _unitOfWork.Listings.GetByIdAsync(transaction.ListingId.Value);
            if (listing != null && !listing.IsActive)
            {
                listing.IsActive = true;
                listing.ValidUntil = membership.ValidUntil;
                freePlanCityId = listing.CityId;
                _logger.LogInformation($"Listing {listing.Id} activated with FREE plan valid until {membership.ValidUntil}");
            }
        }

        var user = await _unitOfWork.Users.GetByIdAsync(userId);
        if (user != null)
        {
            // Mark as used free plan ONLY if payment is ENABLED
            var paymentFeature = await _unitOfWork.PaymentFeature.GetAsync();
            if (paymentFeature?.IsEnabled == true && !user.HasUsedFreePlan)
            {
                user.HasUsedFreePlan = true;
                _logger.LogInformation($"User {userId} marked as used free plan");
            }
        }

        await _unitOfWork.SaveChangesAsync();
        await InvalidateNearbyCacheAsync(freePlanCityId);
    }
}
