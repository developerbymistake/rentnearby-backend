using System.Security.Claims;
using System.Text.Json;
using FluentValidation;
using Mapster;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Services;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

public static class RoleHandlers
{
    public const string RoleUnavailableType = "RoleUnavailable";
    private const string RoleUnavailableMessage = "This role is not available right now.";

    // Only roles whose tab is on; filtered in memory from the cached roles and tabs (no DB on a hit).
    public static async Task<IResult> GetRoles(IUnitOfWork unitOfWork, IMemoryCache cache)
    {
        var roles = await ConfigHandlers.GetAvailableRolesAsync(unitOfWork, cache);
        return OkResponse(roles.Select(r => r.Adapt<RoleDto>()).ToList());
    }

    private static async Task<Role?> FindActiveRoleAsync(string key, IUnitOfWork unitOfWork, IMemoryCache cache)
    {
        var normalized = key.Trim().ToUpperInvariant();
        return (await ConfigHandlers.GetActiveRolesCachedAsync(unitOfWork, cache)).FirstOrDefault(r => r.Key == normalized);
    }

    public static async Task<IResult> ChooseRole(
        RoleKeyRequest request,
        IValidator<RoleKeyRequest> validator,
        ClaimsPrincipal principal,
        IUnitOfWork unitOfWork,
        IJwtService jwtService,
        IMemoryCache cache)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
            return BadRequestResponse(validation.Errors[0].ErrorMessage);

        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        if (!Guid.TryParse(principal.FindFirst("session_id")?.Value, out var sessionId))
            return UnauthorizedResponse();

        var role = await FindActiveRoleAsync(request.Role, unitOfWork, cache);
        if (role == null)
            return BadRequestResponse("Unknown or inactive role.", "InvalidRole");

        var tabs = await ConfigHandlers.GetAppTabsCachedAsync(unitOfWork, cache);
        if (!TabState.IsRoleAvailable(tabs, role))
        {
            // The replay of a choice committed before the tab went off still gets its token.
            var current = await unitOfWork.Users.GetByIdAsync(userId);
            if (current == null) return NotFoundResponse("User not found");
            if (current.RoleId != role.Id || current.RoleConfirmedAt == null)
                return BadRequestResponse(RoleUnavailableMessage, RoleUnavailableType);
            return OkResponse(new AuthResponse
            {
                Token = jwtService.GenerateToken(current, sessionId),
                User = await UsersHandlers.BuildUserDtoAsync(current, unitOfWork, cache)
            });
        }

        var updated = await unitOfWork.Users.ConfirmRoleChoiceAsync(userId, role.Id);

        var user = await unitOfWork.Users.GetByIdAsync(userId);
        if (user == null) return NotFoundResponse("User not found");

        // A replay of the same committed choice (lost response) gets the token again instead of a 409.
        if (updated == 0 && user.RoleId != role.Id)
            return ConflictResponse("Role already chosen.", "RoleAlreadyChosen");

        return OkResponse(new AuthResponse
        {
            Token = jwtService.GenerateToken(user, sessionId),
            User = await UsersHandlers.BuildUserDtoAsync(user, unitOfWork, cache)
        });
    }

    public static async Task<IResult> CreateRoleRequest(
        RoleKeyRequest request,
        IValidator<RoleKeyRequest> validator,
        ClaimsPrincipal principal,
        IUnitOfWork unitOfWork,
        IRabbitMqPublisher publisher,
        IRoleChangeGuardService guard,
        IRateLimitService rateLimiter,
        HttpContext httpContext,
        IMemoryCache cache)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
            return BadRequestResponse(validation.Errors[0].ErrorMessage);

        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var rl = await rateLimiter.CheckAsync($"role:request:{userId}", maxAttempts: 3, window: TimeSpan.FromMinutes(10));
        if (!rl.IsAllowed)
        {
            httpContext.Response.Headers["Retry-After"] = ((int)rl.RetryAfter!.Value.TotalSeconds).ToString();
            return TooManyRequestsResponse();
        }

        var user = await unitOfWork.Users.GetByIdAsync(userId);
        if (user == null) return NotFoundResponse("User not found");

        if (user.RoleConfirmedAt == null)
            return BadRequestResponse("Select your role first.", "RoleNotConfirmed");

        var role = await FindActiveRoleAsync(request.Role, unitOfWork, cache);
        if (role == null)
            return BadRequestResponse("Unknown or inactive role.", "InvalidRole");

        if (role.Id == user.RoleId)
            return BadRequestResponse("This is already your role.", "RoleUnchanged");

        // Leaving a role whose tab is off is always allowed; only the target must be available.
        var tabs = await ConfigHandlers.GetAppTabsCachedAsync(unitOfWork, cache);
        if (!TabState.IsRoleAvailable(tabs, role))
            return BadRequestResponse(RoleUnavailableMessage, RoleUnavailableType);

        var latest = await unitOfWork.RoleChangeRequests.GetLatestForUserAsync(userId);
        if (latest?.Status == RoleChangeRequestStatuses.Pending)
            return ConflictResponse("A role change request is already pending.", "RoleRequestPending");

        if (RoleChangeRules.NextRequestAllowedAt(latest?.Status, latest?.ReviewedAt, DateTime.UtcNow) is DateTime nextAllowedAt)
            return ConflictResponse(RoleChangeRules.CooldownMessage(nextAllowedAt), "RoleRequestCooldown");

        var leftKind = UserRoles.ListingKindOf(user.RoleId);
        if (leftKind != null && await guard.GetBlockingCountAsync(userId, leftKind) > 0)
            return ConflictResponse(RoleChangeRules.LiveListingBlockedMessage(leftKind), RoleChangeRules.LiveListingErrorType);

        var roleRequest = new RoleChangeRequest
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            FromRoleId = user.RoleId,
            ToRoleId = role.Id,
            Status = RoleChangeRequestStatuses.Pending,
            RequestedAt = DateTime.UtcNow
        };
        await unitOfWork.RoleChangeRequests.AddAsync(roleRequest);
        try
        {
            await unitOfWork.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            return ConflictResponse("A role change request is already pending.", "RoleRequestPending");
        }

        await PublishRoleRequestedAsync(publisher, roleRequest.Id, role.Name);

        return CreatedResponse(roleRequest.Adapt<RoleChangeRequestDto>(), "/api/v1/users/me/role-request");
    }

    private static async Task PublishRoleRequestedAsync(IRabbitMqPublisher publisher, Guid requestId, string toRoleName)
    {
        try
        {
            var message = new RoleRequestedMessage { RequestId = requestId, ToRoleName = toRoleName };
            await publisher.PublishAsync("role.requested", JsonSerializer.Serialize(message));
        }
        catch { }
    }

    public static async Task<IResult> GetMyRoleRequest(ClaimsPrincipal principal, IUnitOfWork unitOfWork)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var latest = await unitOfWork.RoleChangeRequests.GetLatestForUserAsync(userId);
        return OkResponse(latest?.Adapt<RoleChangeRequestDto>());
    }
}
