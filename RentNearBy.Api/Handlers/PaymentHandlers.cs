using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Services;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

public static class PaymentHandlers
{
    public static async Task<IResult> InitiatePayment(
        Guid listingId,
        [FromBody] PaymentPlanRequest request,
        ClaimsPrincipal principal,
        IPaymentService paymentService)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.PlanType))
            return BadRequestResponse("Plan type is required");

        if (request.PlanType != "FREE" && request.PlanType != "PAID")
            return BadRequestResponse("Plan type must be FREE or PAID");

        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        try
        {
            var response = await paymentService.InitiatePaymentAsync(userId, listingId, request.PlanType);
            return OkResponse(response);
        }
        catch (ArgumentException)
        {
            return BadRequestResponse("Invalid payment parameters");
        }
        catch (KeyNotFoundException)
        {
            return NotFoundResponse("Listing not found");
        }
        catch (UnauthorizedAccessException)
        {
            return UnauthorizedResponse("You don't have permission to initiate payment for this listing");
        }
        catch (InvalidOperationException ex)
        {
            return BadRequestResponse(ex.Message);
        }
        catch (Exception)
        {
            return ServerErrorResponse();
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
        catch (KeyNotFoundException)
        {
            return NotFoundResponse("Transaction not found or listing not found");
        }
        catch (UnauthorizedAccessException)
        {
            return UnauthorizedResponse("You don't have permission to verify this payment");
        }
        catch (InvalidOperationException ex)
        {
            return BadRequestResponse(ex.Message);
        }
        catch (Exception)
        {
            return ServerErrorResponse();
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
        catch (Exception)
        {
            return ServerErrorResponse();
        }
    }
}
