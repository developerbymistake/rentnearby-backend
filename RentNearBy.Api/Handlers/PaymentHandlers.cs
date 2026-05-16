using System.Security.Claims;
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
        IPaymentService paymentService)
    {
        if (string.IsNullOrWhiteSpace(planType))
            return BadRequestResponse("Plan type is required");

        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        try
        {
            var response = await paymentService.InitiatePaymentAsync(userId, listingId, planType);
            return OkResponse(response);
        }
        catch (ArgumentException ex)
        {
            return BadRequestResponse(ex.Message);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFoundResponse(ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return UnauthorizedResponse(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequestResponse(ex.Message);
        }
        catch (Exception ex)
        {
            return ServerErrorResponse($"Payment initiation failed: {ex.Message}");
        }
    }

    public static async Task<IResult> VerifyPayment(
        Guid listingId,
        VerifyPaymentRequest request,
        ClaimsPrincipal principal,
        IPaymentService paymentService)
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
            return OkResponse(response);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFoundResponse(ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return UnauthorizedResponse(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequestResponse(ex.Message);
        }
        catch (Exception ex)
        {
            return ServerErrorResponse($"Payment verification failed: {ex.Message}");
        }
    }

    public static async Task<IResult> GetMembershipStatus(
        ClaimsPrincipal principal,
        IPaymentService paymentService,
        IUnitOfWork unitOfWork)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        try
        {
            var membership = await unitOfWork.UserMemberships.GetActiveByUserIdAsync(userId);
            var activeRooms = await paymentService.GetActiveRoomCountAsync(userId);
            var canActivate = await paymentService.CanUserActivateListingAsync(userId);

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
            return ServerErrorResponse($"Failed to fetch membership status: {ex.Message}");
        }
    }
}
