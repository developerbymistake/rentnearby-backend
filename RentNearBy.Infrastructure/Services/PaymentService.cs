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

    public PaymentService(IUnitOfWork unitOfWork, IRazorpayService razorpay)
    {
        _unitOfWork = unitOfWork;
        _razorpay = razorpay;
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
            var (orderId, _) = await _razorpay.CreateOrderAsync(amount, transaction.Id.ToString());
            transaction.RazorpayOrderId = orderId;
        }

        await _unitOfWork.PaymentTransactions.AddAsync(transaction);
        await _unitOfWork.SaveChangesAsync();

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
        var transaction = await _unitOfWork.PaymentTransactions.GetByRazorpayOrderIdAsync(request.RazorpayOrderId);
        if (transaction == null)
            throw new KeyNotFoundException("Transaction not found.");
        if (transaction.UserId != userId)
            throw new UnauthorizedAccessException("Transaction doesn't belong to you.");

        if (transaction.Status == "SUCCESS")
            throw new InvalidOperationException("Transaction already processed.");

        if (transaction.PlanType == "PAID")
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

        var paymentFeature = await _unitOfWork.PaymentFeature.GetAsync();
        var membership = new UserMembership
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            PlanType = transaction.PlanType,
            ValidFrom = DateTime.UtcNow,
            ValidUntil = transaction.PlanType == "FREE"
                ? DateTime.UtcNow.AddDays(paymentFeature!.FreePlanDays)
                : DateTime.UtcNow.AddDays(paymentFeature!.PaidPlanDays),
            MaxRooms = transaction.PlanType == "FREE"
                ? paymentFeature.FreePlanRoomLimit
                : paymentFeature.PaidPlanRoomLimit,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await _unitOfWork.UserMemberships.AddAsync(membership);

        if (transaction.ListingId.HasValue)
        {
            var listing = await _unitOfWork.Listings.GetByIdAsync(transaction.ListingId.Value);
            if (listing != null)
            {
                listing.IsActive = true;
                listing.ValidUntil = membership.ValidUntil;
            }
        }

        var user = await _unitOfWork.Users.GetByIdAsync(userId);
        if (transaction.PlanType == "FREE")
            user!.HasUsedFreePlan = true;

        await _unitOfWork.SaveChangesAsync();

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

    public async Task<bool> CanUserActivateListingAsync(Guid userId)
    {
        var membership = await _unitOfWork.UserMemberships.GetActiveByUserIdAsync(userId);
        if (membership == null)
            return true;

        var activeRooms = await GetActiveRoomCountAsync(userId);
        return activeRooms < membership.MaxRooms;
    }

    public async Task<int> GetActiveRoomCountAsync(Guid userId)
    {
        var listings = await _unitOfWork.Listings.GetByUserIdAsync(userId);
        return listings.Count(l => l.IsActive && !l.IsDeleted);
    }
}
