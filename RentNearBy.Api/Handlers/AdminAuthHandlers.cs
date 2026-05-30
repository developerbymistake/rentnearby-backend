using System.Security.Claims;
using FluentValidation;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Services;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

public static class AdminAuthHandlers
{
    private static readonly TimeSpan OtpWindow = TimeSpan.FromMinutes(10);
    private const int OtpSendMax = 3;
    private const int OtpVerifyMax = 5;

    public static async Task<IResult> AdminLogin(
        AdminLoginRequest request,
        IValidator<AdminLoginRequest> validator,
        IUnitOfWork unitOfWork,
        IJwtService jwtService)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
            return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var admin = await unitOfWork.Admins.GetByEmailAsync(request.Email);
        if (admin == null || !BCrypt.Net.BCrypt.Verify(request.Password, admin.PasswordHash))
            return BadRequestResponse("Invalid email or password", "InvalidCredentials");

        if (!admin.IsActive)
            return ForbiddenResponse("Admin account is inactive. Contact support.");

        return await CreateAdminSessionAndRespond(admin, unitOfWork, jwtService);
    }

    public static async Task<IResult> AdminResetPassword(
        AdminResetPasswordRequest request,
        IValidator<AdminResetPasswordRequest> validator,
        IUnitOfWork unitOfWork,
        IJwtService jwtService,
        IRateLimitService rateLimiter,
        IOtpService otpService,
        HttpContext httpContext)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
            return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var rl = await rateLimiter.CheckAsync($"admin_otp:verify:{request.PhoneNumber}", OtpVerifyMax, OtpWindow);
        if (!rl.IsAllowed)
        {
            httpContext.Response.Headers["Retry-After"] = ((int)rl.RetryAfter!.Value.TotalSeconds).ToString();
            return TooManyRequestsResponse();
        }

        if (!await otpService.VerifyOtpAsync(request.PhoneNumber, request.Otp))
            return BadRequestResponse("Invalid OTP", "InvalidOtp");

        var admin = await unitOfWork.Admins.GetByPhoneAsync(request.PhoneNumber);
        if (admin == null || !admin.IsActive)
            return NotFoundResponse("Admin not found");

        admin.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword, 12);
        admin.UpdatedAt = DateTime.UtcNow;
        await unitOfWork.Admins.UpdateAsync(admin);
        await unitOfWork.SaveChangesAsync();

        return await CreateAdminSessionAndRespond(admin, unitOfWork, jwtService);
    }

    public static async Task<IResult> AdminLogout(ClaimsPrincipal principal, IUnitOfWork unitOfWork)
    {
        var adminIdClaim = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(adminIdClaim, out var adminId))
            return UnauthorizedResponse();

        var sessionIdClaim = principal.FindFirst("session_id")?.Value;
        if (Guid.TryParse(sessionIdClaim, out var sessionId))
        {
            var session = await unitOfWork.AdminSessions.GetByIdAsync(sessionId);
            if (session != null && session.AdminId == adminId)
            {
                await unitOfWork.AdminSessions.DeleteAsync(session);
                await unitOfWork.SaveChangesAsync();
            }
        }
        return OkResponse(new { message = "Logged out successfully" });
    }

    private static async Task<IResult> CreateAdminSessionAndRespond(Admin admin, IUnitOfWork unitOfWork, IJwtService jwtService)
    {
        await unitOfWork.AdminSessions.DeleteAllAdminSessionsAsync(admin.Id);

        var session = new AdminSession
        {
            Id = Guid.NewGuid(),
            AdminId = admin.Id,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(365)
        };
        await unitOfWork.AdminSessions.AddAsync(session);
        await unitOfWork.SaveChangesAsync();

        var token = jwtService.GenerateAdminToken(admin, session.Id);
        return OkResponse(new
        {
            token,
            admin = new { id = admin.Id, email = admin.Email, phoneNumber = admin.PhoneNumber, name = admin.Name }
        });
    }
}
