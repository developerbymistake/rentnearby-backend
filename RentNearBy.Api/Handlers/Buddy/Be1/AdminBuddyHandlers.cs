using RentNearBy.Core.DTOs.Buddy;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Infrastructure.Services.Buddy.Be1;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers.Buddy.Be1;

// PUT /admin/users/{id}/buddy-hold (AdminOnly).
public static class AdminBuddyHandlers
{
    public static async Task<IResult> SetHold(Guid id, BuddyHoldRequest request, UserReportService reports, CancellationToken ct)
    {
        if (!BuddyRules.IsValidHold(request.Until?.ToUniversalTime(), DateTime.UtcNow))
            return BadRequestResponse("Pick a date within the next 365 days");
        var hold = await reports.SetHoldAsync(id, request.Until?.ToUniversalTime(), ct);
        return hold is null ? NotFoundResponse("User not found") : OkResponse(hold);
    }
}
