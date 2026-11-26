using System.Security.Claims;
using FluentValidation;
using RentNearBy.Core.DTOs.Social;
using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Services.Social;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers.Social;

// Contract 2.8: admin snapshot read, trip post unpublish, identity correction (AdminOnly).
public static class SocialAdminHandlers
{
    public static async Task<IResult> Snapshot(Guid id, ClaimsPrincipal principal, SocialReportService reports, CancellationToken ct)
    {
        var adminId = AdminId(principal);
        return await reports.AdminSnapshotAsync(adminId, id, ct) is { } dto ? OkResponse(dto) : NotFoundResponse("No chat snapshot for this report");
    }

    public static async Task<IResult> Unpublish(Guid tripId, PublicTripService posts, CancellationToken ct)
    {
        await posts.UnpublishAsync(null, tripId, ct);
        return NoContentResponse();
    }

    public static async Task<IResult> SetIdentity(
        Guid id, AdminIdentityRequest request, IValidator<AdminIdentityRequest> validator, ClaimsPrincipal principal,
        SocialProfileService profiles, CancellationToken ct)
    {
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid) return SocialHttp.Invalid(validation);
        var result = await profiles.AdminSetIdentityAsync(AdminId(principal), id, request, ct);
        if (!result.Ok && result.ErrorType == SocialErrorTypes.ProfileRequired) return NotFoundResponse("This user has no social profile");
        return SocialHttp.From(result);
    }

    private static Guid AdminId(ClaimsPrincipal principal) => UsersHandlers.TryGetUserId(principal, out var id) ? id : Guid.Empty;
}
