using System.Security.Claims;
using FluentValidation;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

// New trip map switch (AppFeatureKeys.MapDiscoveryEnabled row, seeded on). Same body as /admin/trip-execution.
public static class AdminMapDiscoveryHandlers
{
    public static async Task<IResult> Get(IUnitOfWork unitOfWork)
    {
        var flag = await unitOfWork.AppFeatureFlags.GetByKeyAsync(AppFeatureKeys.MapDiscoveryEnabled);
        if (flag == null) return NotFoundResponse("Map discovery setting not found");

        return OkResponse(ToDto(flag));
    }

    private static object ToDto(AppFeatureFlag flag) => new
    {
        isEnabled = flag.IsEnabled,
        updatedByAdminId = flag.UpdatedByAdminId,
        updatedAt = flag.UpdatedAt,
        reason = flag.Reason,
    };

    public static async Task<IResult> Update(
        UpdateTripExecutionRequest request, IValidator<UpdateTripExecutionRequest> validator,
        ClaimsPrincipal principal, IUnitOfWork unitOfWork, IMapDiscoveryFeatureReader reader, ILoggerFactory loggerFactory)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        var flag = await unitOfWork.AppFeatureFlags.GetByKeyAsync(AppFeatureKeys.MapDiscoveryEnabled);
        if (flag == null) return NotFoundResponse("Map discovery setting not found");

        flag.IsEnabled = request.IsEnabled!.Value;
        flag.Reason = request.Reason;
        var adminIdClaim = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        flag.UpdatedByAdminId = Guid.TryParse(adminIdClaim, out var adminId) ? adminId : null;
        flag.UpdatedAt = DateTime.UtcNow;

        await unitOfWork.AppFeatureFlags.UpdateAsync(flag);
        await unitOfWork.SaveChangesAsync();

        reader.Invalidate();
        loggerFactory.CreateLogger("MapDiscoverySwitch")
            .LogInformation("Map discovery switched to {Enabled} by admin {AdminId}", flag.IsEnabled, flag.UpdatedByAdminId);

        return OkResponse(ToDto(flag));
    }
}
