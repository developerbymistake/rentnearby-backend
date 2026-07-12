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

    public static async Task<IResult> UpdateProfile(
        UpdateProfileRequest request,
        ClaimsPrincipal principal,
        IValidator<UpdateProfileRequest> validator,
        IUnitOfWork unitOfWork)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid)
            return BadRequestResponse(validation.Errors[0].ErrorMessage);

        if (!TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var user = await unitOfWork.Users.GetByIdAsync(userId);
        if (user == null) return NotFoundResponse("User not found");

        if (request.Name != null) user.Name = request.Name;
        if (request.IsContactVisible.HasValue) user.IsContactVisible = request.IsContactVisible.Value;
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
