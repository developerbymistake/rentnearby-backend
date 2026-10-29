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
using RentNearBy.Infrastructure.Data;
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

    // One-time self-service fix of a wrong Stay/Taxi pick: allowed only while that setup was never submitted.
    public static async Task<IResult> CorrectRole(
        RoleKeyRequest request,
        IValidator<RoleKeyRequest> validator,
        ClaimsPrincipal principal,
        IUnitOfWork unitOfWork,
        ApplicationDbContext db,
        IListingPurgeService listingPurge,
        IPlanPurchaseService purchases,
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

        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
        if (user == null) return NotFoundResponse("User not found");

        if (user.RoleConfirmedAt == null)
            return BadRequestResponse("Select your role first.", "RoleNotConfirmed");

        var role = await FindActiveRoleAsync(request.Role, unitOfWork, cache);
        if (role == null)
            return BadRequestResponse("Unknown or inactive role.", "InvalidRole");

        // A replay of the committed correction (lost response) gets the token again.
        if (user.RoleCorrectionUsed && user.RoleId == role.Id)
            return await ReplayCorrectionAsync(user, sessionId, unitOfWork, jwtService, cache);

        if (user.RoleCorrectionUsed)
            return ConflictResponse(RoleCorrectionUsedMessage, RoleCorrectionUsedType);

        if (role.Id == user.RoleId)
            return BadRequestResponse("This is already your role.", "RoleUnchanged");

        var tabs = await ConfigHandlers.GetAppTabsCachedAsync(unitOfWork, cache);
        if (!TabState.IsRoleAvailable(tabs, role))
            return BadRequestResponse(RoleUnavailableMessage, RoleUnavailableType);

        var leftKind = UserRoles.ListingKindOf(user.RoleId);
        if (leftKind is not (ListingKinds.Stay or ListingKinds.Taxi))
            return ConflictResponse(RoleCorrectionNotAllowedMessage, RoleCorrectionNotAllowedType);

        var latest = await unitOfWork.RoleChangeRequests.GetLatestForUserAsync(userId);
        if (latest?.Status == RoleChangeRequestStatuses.Pending)
            return ConflictResponse(RoleRequestPendingMessage, RoleRequestPendingType);

        if (await HasSubmittedHistoryAsync(unitOfWork, userId, leftKind))
            return ConflictResponse(RoleCorrectionNotAllowedMessage, RoleCorrectionNotAllowedType);

        IReadOnlyList<Guid> purgedIds;
        Guid newSessionId;
        await unitOfWork.BeginTransactionAsync();
        try
        {
            // Owner lock first, same order as a plan grant, so the two never interleave.
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Users\" WHERE \"Id\" = {userId} FOR UPDATE");
            var updated = await unitOfWork.Users.CorrectRoleAsync(userId, user.RoleId, role.Id);
            if (updated == 0)
            {
                await unitOfWork.RollbackTransactionAsync();
                var current = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
                if (current == null) return NotFoundResponse("User not found");
                if (current.RoleCorrectionUsed && current.RoleId == role.Id)
                    return await ReplayCorrectionAsync(current, sessionId, unitOfWork, jwtService, cache);
                return ConflictResponse(RoleCorrectionUsedMessage, RoleCorrectionUsedType);
            }

            // Lock the kind's rows and bump xmin first, so a racing Submit either shows up here or fails on save.
            if (leftKind == ListingKinds.Stay)
                await unitOfWork.Stays.TouchAllByUserAsync(userId);
            else
                await unitOfWork.Taxis.TouchAllByUserAsync(userId);

            if (await HasSubmittedHistoryAsync(unitOfWork, userId, leftKind))
            {
                await unitOfWork.RollbackTransactionAsync();
                return ConflictResponse(RoleCorrectionNotAllowedMessage, RoleCorrectionNotAllowedType);
            }

            // A request created before our user-row lock is visible now; one created later fails its own role lock.
            var pending = await unitOfWork.RoleChangeRequests.GetLatestForUserAsync(userId);
            if (pending?.Status == RoleChangeRequestStatuses.Pending)
            {
                await unitOfWork.RollbackTransactionAsync();
                return ConflictResponse(RoleRequestPendingMessage, RoleRequestPendingType);
            }

            await purchases.CancelActivePlansAsync(userId, leftKind, "Role corrected");
            purgedIds = await listingPurge.DeleteUserListingRowsAsync(userId, leftKind);

            // Every token with the old role claim, the caller's included, dies here; the caller gets a new session.
            var rotated = await RotateSessionAsync(unitOfWork, userId, sessionId);
            if (rotated == null)
            {
                await unitOfWork.RollbackTransactionAsync();
                return UnauthorizedResponse();
            }
            newSessionId = rotated.Value;

            await unitOfWork.CommitTransactionAsync();
        }
        catch
        {
            await unitOfWork.RollbackTransactionAsync();
            throw;
        }

        await listingPurge.DeleteListingPhotosBestEffortAsync(userId, purgedIds);

        var fresh = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
        if (fresh == null) return NotFoundResponse("User not found");
        return await CorrectionResponseAsync(fresh, newSessionId, unitOfWork, jwtService, cache);
    }

    private const string RoleCorrectionUsedType = "RoleCorrectionUsed";
    private const string RoleCorrectionUsedMessage = "You already changed your role once.";
    private const string RoleCorrectionNotAllowedType = "RoleCorrectionNotAllowed";
    private const string RoleCorrectionNotAllowedMessage = "Role can be changed only before submit.";
    private const string RoleRequestPendingType = "RoleRequestPending";
    private const string RoleRequestPendingMessage = "A role change request is already pending.";

    // Runs inside the caller's transaction. Null when the caller's session is no longer active.
    private static async Task<Guid?> RotateSessionAsync(IUnitOfWork unitOfWork, Guid userId, Guid callerSessionId)
    {
        if (await unitOfWork.Sessions.RevokeSessionAsync(userId, callerSessionId) == 0)
            return null;
        await unitOfWork.Sessions.RevokeAllUserSessionsAsync(userId);

        var now = DateTime.UtcNow;
        var session = new Session
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            CreatedAt = now,
            ExpiresAt = now.AddDays(365)
        };
        await unitOfWork.Sessions.AddAsync(session);
        await unitOfWork.SaveChangesAsync();
        return session.Id;
    }

    // A replay after a committed correction: an active caller session is rotated, a revoked one is 401.
    private static async Task<IResult> ReplayCorrectionAsync(
        User user, Guid sessionId, IUnitOfWork unitOfWork, IJwtService jwtService, IMemoryCache cache)
    {
        Guid? rotated;
        await unitOfWork.BeginTransactionAsync();
        try
        {
            rotated = await RotateSessionAsync(unitOfWork, user.Id, sessionId);
            if (rotated == null)
            {
                await unitOfWork.RollbackTransactionAsync();
                return UnauthorizedResponse();
            }
            await unitOfWork.CommitTransactionAsync();
        }
        catch
        {
            await unitOfWork.RollbackTransactionAsync();
            throw;
        }
        return await CorrectionResponseAsync(user, rotated.Value, unitOfWork, jwtService, cache);
    }

    private static Task<bool> HasSubmittedHistoryAsync(IUnitOfWork unitOfWork, Guid userId, string kind)
        => kind == ListingKinds.Stay
            ? unitOfWork.Stays.HasSubmittedHistoryAsync(userId)
            : unitOfWork.Taxis.HasSubmittedHistoryAsync(userId);

    private static async Task<IResult> CorrectionResponseAsync(
        User user, Guid sessionId, IUnitOfWork unitOfWork, IJwtService jwtService, IMemoryCache cache)
        => OkResponse(new AuthResponse
        {
            Token = jwtService.GenerateToken(user, sessionId),
            User = await UsersHandlers.BuildUserDtoAsync(user, unitOfWork, cache)
        });

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
            return ConflictResponse(RoleRequestPendingMessage, RoleRequestPendingType);

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
        // The user-row lock serialises this insert with a role correction, so neither can slip past the other.
        await unitOfWork.BeginTransactionAsync();
        try
        {
            if (await unitOfWork.Users.LockIfRoleAsync(userId, user.RoleId) == 0)
            {
                await unitOfWork.RollbackTransactionAsync();
                return ConflictResponse("Your role has changed. Reload and try again.", "RoleChanged");
            }
            await unitOfWork.RoleChangeRequests.AddAsync(roleRequest);
            await unitOfWork.SaveChangesAsync();
            await unitOfWork.CommitTransactionAsync();
        }
        catch (DbUpdateException)
        {
            await unitOfWork.RollbackTransactionAsync();
            return ConflictResponse(RoleRequestPendingMessage, RoleRequestPendingType);
        }
        catch
        {
            await unitOfWork.RollbackTransactionAsync();
            throw;
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
