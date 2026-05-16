using Microsoft.Extensions.Logging;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;

namespace RentNearBy.Infrastructure.Services;

public interface IPaymentService
{
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

    public PaymentService(IUnitOfWork unitOfWork, IRazorpayService razorpay, ILogger<PaymentService> logger)
    {
        _unitOfWork = unitOfWork;
        _razorpay = razorpay;
        _logger = logger;
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

        // Prevent FREE plan reuse: check if user already used free plan
        if (planType == "FREE")
        {
            var user = await _unitOfWork.Users.GetByIdAsync(userId);
            if (user?.HasUsedFreePlan == true)
            {
                _logger.LogWarning($"User {userId} attempted to use FREE plan again");
                throw new InvalidOperationException("You have already used the free plan. Please use the paid plan.");
            }
        }

        var amount = planType == "FREE" ? 0 : paymentFeature.PaidPlanPrice;
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
            {
                _logger.LogError($"Payment feature configuration not found");
                throw new InvalidOperationException("Payment system not configured.");
            }

            // Determine ValidUntil based on payment feature status
            DateTime validUntil;
            if (!paymentFeature.IsEnabled)
            {
                // Payment disabled: use FreeListingDaysWhenDisabled (null = indefinite/year 2099)
                var daysToAdd = paymentFeature.FreeListingDaysWhenDisabled ?? 365;
                validUntil = DateTime.UtcNow.AddDays(daysToAdd);
                _logger.LogInformation($"Payment disabled. Using FreeListingDaysWhenDisabled={paymentFeature.FreeListingDaysWhenDisabled} for listing validity");
            }
            else
            {
                // Payment enabled: use plan-based duration
                validUntil = transaction.PlanType == "FREE"
                    ? DateTime.UtcNow.AddDays(paymentFeature.FreePlanDays)
                    : DateTime.UtcNow.AddDays(paymentFeature.PaidPlanDays);
            }

            var membership = new UserMembership
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                PlanType = transaction.PlanType,
                ValidFrom = DateTime.UtcNow,
                ValidUntil = validUntil,
                MaxRooms = transaction.PlanType == "FREE"
                    ? paymentFeature.FreePlanRoomLimit
                    : paymentFeature.PaidPlanRoomLimit,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            await _unitOfWork.UserMemberships.AddAsync(membership);

            // Validate and activate listing: check it hasn't been activated already
            if (transaction.ListingId.HasValue)
            {
                var listing = await _unitOfWork.Listings.GetByIdAsync(transaction.ListingId.Value);
                if (listing != null)
                {
                    if (listing.IsActive)
                    {
                        _logger.LogWarning($"Listing {listing.Id} is already active");
                        throw new InvalidOperationException("This listing is already active. No further action needed.");
                    }

                    listing.IsActive = true;
                    listing.ValidUntil = membership.ValidUntil;
                    _logger.LogInformation($"Listing {listing.Id} activated with membership valid until {membership.ValidUntil}");
                }
            }

            var user = await _unitOfWork.Users.GetByIdAsync(userId);
            if (user == null)
            {
                _logger.LogError($"User {userId} not found during payment verification");
                throw new KeyNotFoundException("User not found.");
            }

            // Mark user as used free plan (idempotent operation)
            if (transaction.PlanType == "FREE" && !user.HasUsedFreePlan)
            {
                user.HasUsedFreePlan = true;
                _logger.LogInformation($"User {userId} marked as used free plan");
            }

            await _unitOfWork.SaveChangesAsync();

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
}
