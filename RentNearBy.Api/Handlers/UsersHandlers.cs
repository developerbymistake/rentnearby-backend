using System.Security.Claims;
using FluentValidation;
using Mapster;
using Microsoft.Extensions.Caching.Memory;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

public static class UsersHandlers
{
    public static async Task<IResult> GetProfile(ClaimsPrincipal principal, IUnitOfWork unitOfWork, IMemoryCache cache)
    {
        if (!TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var user = await unitOfWork.Users.GetByIdAsync(userId);
        if (user == null) return NotFoundResponse("User not found");

        return OkResponse(await BuildUserDtoAsync(user, unitOfWork, cache));
    }

    // Two independent identity/privacy actions, two independent endpoints — was previously one
    // combined UpdateProfile handler that always required Name even when only the visibility
    // toggle changed. Each is its own user-facing action in the app (edit-name sheet vs.
    // visibility-confirm sheet) with its own validation shape, so they get their own routes
    // rather than one generic endpoint doing partial updates via nullable fields.
    public static async Task<IResult> UpdateName(
        UpdateNameRequest request,
        ClaimsPrincipal principal,
        IValidator<UpdateNameRequest> validator,
        IUnitOfWork unitOfWork,
        IMemoryCache cache)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
            return BadRequestResponse(validation.Errors[0].ErrorMessage);

        if (!TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var user = await unitOfWork.Users.GetByIdAsync(userId);
        if (user == null) return NotFoundResponse("User not found");

        user.Name = request.Name!;
        user.UpdatedAt = DateTime.UtcNow;

        await unitOfWork.Users.UpdateAsync(user);
        await unitOfWork.SaveChangesAsync();

        return OkResponse(await BuildUserDtoAsync(user, unitOfWork, cache));
    }

    public static async Task<IResult> UpdateContactVisibility(
        UpdateContactVisibilityRequest request,
        ClaimsPrincipal principal,
        IValidator<UpdateContactVisibilityRequest> validator,
        IUnitOfWork unitOfWork,
        IMemoryCache cache)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
            return BadRequestResponse(validation.Errors[0].ErrorMessage);

        if (!TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var user = await unitOfWork.Users.GetByIdAsync(userId);
        if (user == null) return NotFoundResponse("User not found");

        var visible = request.IsContactVisible!.Value;
        if (!visible && UserRoles.ListingKindOf(user.RoleId) != null)
            return ConflictResponse("Owners keep their contact number visible.", "ContactVisibilityLocked");

        user.IsContactVisible = visible;
        user.UpdatedAt = DateTime.UtcNow;

        await unitOfWork.Users.UpdateAsync(user);
        await unitOfWork.SaveChangesAsync();

        return OkResponse(await BuildUserDtoAsync(user, unitOfWork, cache));
    }

    public static async Task<IResult> GetMyReports(
        ClaimsPrincipal principal, IUnitOfWork unitOfWork, int page = 1, int pageSize = 20)
    {
        if (!TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        pageSize = Math.Clamp(pageSize, 1, 50);
        page = Math.Max(1, page);
        var paged = await unitOfWork.ListingReports.GetPagedForReporterAsync(userId, page, pageSize);
        var items = paged.Items.Select(r => new ListingReportDto
        {
            Id = r.Id,
            ListingId = r.ListingId,
            ListingType = r.ListingType,
            ReasonName = r.Reason?.Name ?? "",
            Details = r.Details,
            Status = r.Status,
            ResolutionAction = r.ResolutionAction,
            CreatedAt = r.CreatedAt,
            ResolvedAt = r.ResolvedAt,
        }).ToList();
        return OkResponse(new { items, hasMore = paged.HasMore });
    }

    internal static async Task<UserDto> BuildUserDtoAsync(User user, IUnitOfWork unitOfWork, IMemoryCache cache)
    {
        var dto = user.Adapt<UserDto>();
        dto.RoleTabActive = TabState.IsActive(await ConfigHandlers.GetAppTabsCachedAsync(unitOfWork, cache), UserRoles.TabKeyOf(user.RoleId));
        var latest = await unitOfWork.RoleChangeRequests.GetLatestForUserAsync(user.Id);
        dto.RoleRequestStatus = latest?.Status;
        dto.RoleRequestReason = latest?.Status == RoleChangeRequestStatuses.Rejected ? latest.RejectionReason : null;
        dto.NextRoleRequestAt = RoleChangeRules.NextRequestAllowedAt(latest?.Status, latest?.ReviewedAt, DateTime.UtcNow);
        if (user.RoleId == UserRoles.StayOwnerId)
            dto.StayStatus = await unitOfWork.Stays.GetStatusByUserAsync(user.Id);
        if (user.RoleId == UserRoles.TaxiDriverId)
            dto.TaxiStatus = await unitOfWork.Taxis.GetStatusByUserAsync(user.Id);
        return dto;
    }

    internal static bool TryGetUserId(ClaimsPrincipal principal, out Guid userId)
    {
        userId = Guid.Empty;
        var value = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return value != null && Guid.TryParse(value, out userId);
    }
}
