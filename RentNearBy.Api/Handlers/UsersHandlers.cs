using System.Security.Claims;
using FluentValidation;
using Mapster;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Interfaces;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

public static class UsersHandlers
{
    public static async Task<IResult> GetProfile(ClaimsPrincipal principal, IUnitOfWork unitOfWork)
    {
        if (!TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var user = await unitOfWork.Users.GetByIdAsync(userId);
        if (user == null) return NotFoundResponse("User not found");

        return OkResponse(user.Adapt<UserDto>());
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
        IUnitOfWork unitOfWork)
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

        return OkResponse(user.Adapt<UserDto>());
    }

    public static async Task<IResult> UpdateContactVisibility(
        UpdateContactVisibilityRequest request,
        ClaimsPrincipal principal,
        IValidator<UpdateContactVisibilityRequest> validator,
        IUnitOfWork unitOfWork)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
            return BadRequestResponse(validation.Errors[0].ErrorMessage);

        if (!TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var user = await unitOfWork.Users.GetByIdAsync(userId);
        if (user == null) return NotFoundResponse("User not found");

        user.IsContactVisible = request.IsContactVisible!.Value;
        user.UpdatedAt = DateTime.UtcNow;

        await unitOfWork.Users.UpdateAsync(user);
        await unitOfWork.SaveChangesAsync();

        return OkResponse(user.Adapt<UserDto>());
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

    internal static bool TryGetUserId(ClaimsPrincipal principal, out Guid userId)
    {
        userId = Guid.Empty;
        var value = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return value != null && Guid.TryParse(value, out userId);
    }
}
