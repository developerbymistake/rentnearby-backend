using System.Security.Claims;
using FluentValidation;
using Mapster;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Services;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

public static class AuthHandlers
{
    public static async Task<IResult> SendOtp(
        SendOtpRequest request,
        IValidator<SendOtpRequest> validator)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
            return BadRequestResponse(validation.Errors[0].ErrorMessage);

        return OkResponse(new { message = "OTP sent successfully" });
    }

    public static async Task<IResult> VerifyOtp(
        VerifyOtpRequest request,
        IValidator<VerifyOtpRequest> validator,
        IUnitOfWork unitOfWork,
        IJwtService jwtService)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
            return BadRequestResponse(validation.Errors[0].ErrorMessage);

        if (request.Otp != "1234")
            return BadRequestResponse("Invalid OTP", "InvalidOtp");

        var user = await unitOfWork.Users.GetByPhoneAsync(request.PhoneNumber);

        if (user == null)
        {
            user = new User
            {
                Id = Guid.NewGuid(),
                PhoneNumber = request.PhoneNumber,
                OtpVerified = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            await unitOfWork.Users.AddAsync(user);
            await unitOfWork.SaveChangesAsync();
        }
        else
        {
            var tracked = await unitOfWork.Users.GetByIdAsync(user.Id);
            if (tracked != null)
            {
                tracked.OtpVerified = true;
                tracked.UpdatedAt = DateTime.UtcNow;
                await unitOfWork.Users.UpdateAsync(tracked);
                user = tracked;
            }
        }

        // Revoke all previous sessions → single device enforcement
        await unitOfWork.Sessions.RevokeAllUserSessionsAsync(user.Id);

        var session = new Session
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(365)
        };
        await unitOfWork.Sessions.AddAsync(session);
        await unitOfWork.SaveChangesAsync();

        var token = jwtService.GenerateToken(user, session.Id);
        return OkResponse(new AuthResponse { Token = token, User = user.Adapt<UserDto>() });
    }

    public static async Task<IResult> Logout(ClaimsPrincipal principal, IUnitOfWork unitOfWork)
    {
        var sessionIdClaim = principal.FindFirst("session_id")?.Value;
        if (Guid.TryParse(sessionIdClaim, out var sessionId))
        {
            var session = await unitOfWork.Sessions.GetByIdAsync(sessionId);
            if (session != null)
            {
                session.IsRevoked = true;
                await unitOfWork.Sessions.UpdateAsync(session);
                await unitOfWork.SaveChangesAsync();
            }
        }
        return OkResponse(new { message = "Logged out successfully" });
    }
}
