using System.Security.Claims;
using Microsoft.Extensions.Logging;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Services;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

public static class PaymentHandlers
{
    public static async Task<IResult> InitiatePayment(
        Guid listingId,
        string planType,
        ClaimsPrincipal principal,
        IPaymentService paymentService,
        ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(planType))
            return BadRequestResponse("Plan type is required");

        if (planType != "FREE" && planType != "PAID")
            return BadRequestResponse("Plan type must be FREE or PAID");

        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        try
        {
            var response = await paymentService.InitiatePaymentAsync(userId, listingId, planType);
            return OkResponse(response);
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning($"Invalid argument during payment initiation: {ex.Message}");
            return BadRequestResponse("Invalid payment parameters");
        }
        catch (KeyNotFoundException ex)
        {
            logger.LogWarning($"Resource not found during payment initiation: {ex.Message}");
            return NotFoundResponse("Listing not found");
        }
        catch (UnauthorizedAccessException)
        {
            logger.LogWarning($"Unauthorized payment initiation attempt for user {userId}");
            return UnauthorizedResponse("You don't have permission to initiate payment for this listing");
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning($"Invalid operation during payment initiation: {ex.Message}");
            return BadRequestResponse(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError($"Unexpected error during payment initiation: {ex.Message}", ex);
            return ServerErrorResponse();
        }
    }

    public static async Task<IResult> VerifyPayment(
        Guid listingId,
        VerifyPaymentRequest request,
        ClaimsPrincipal principal,
        IPaymentService paymentService,
        ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(request.RazorpayOrderId) ||
            string.IsNullOrWhiteSpace(request.RazorpayPaymentId) ||
            string.IsNullOrWhiteSpace(request.RazorpaySignature))
            return BadRequestResponse("Missing payment verification details");

        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        try
        {
            var response = await paymentService.VerifyAndActivateAsync(userId, request);
            logger.LogInformation($"Payment verified successfully for user {userId}");
            return OkResponse(response);
        }
        catch (KeyNotFoundException ex)
        {
            logger.LogWarning($"Resource not found during payment verification: {ex.Message}");
            return NotFoundResponse("Transaction not found or listing not found");
        }
        catch (UnauthorizedAccessException)
        {
            logger.LogWarning($"Unauthorized payment verification attempt for user {userId}");
            return UnauthorizedResponse("You don't have permission to verify this payment");
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning($"Invalid operation during payment verification: {ex.Message}");
            return BadRequestResponse(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError($"Unexpected error during payment verification: {ex.Message}", ex);
            return ServerErrorResponse();
        }
    }

    public static async Task<IResult> GetMembershipStatus(
        ClaimsPrincipal principal,
        IPaymentService paymentService,
        IUnitOfWork unitOfWork,
        ILogger logger)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        try
        {
            var membership = await unitOfWork.UserMemberships.GetActiveByUserIdAsync(userId);
            var activeRooms = await paymentService.GetActiveRoomCountAsync(userId);
            var canActivate = await paymentService.CanUserActivateListingAsync(userId);

            logger.LogInformation($"Membership status retrieved for user {userId}: has_membership={membership != null}, active_rooms={activeRooms}");

            return OkResponse(new
            {
                hasMembership = membership != null,
                planType = membership?.PlanType,
                validUntil = membership?.ValidUntil,
                maxRooms = membership?.MaxRooms ?? 0,
                activeRooms,
                canActivate
            });
        }
        catch (Exception ex)
        {
            logger.LogError($"Error fetching membership status for user {userId}: {ex.Message}", ex);
            return ServerErrorResponse();
        }
    }
}
