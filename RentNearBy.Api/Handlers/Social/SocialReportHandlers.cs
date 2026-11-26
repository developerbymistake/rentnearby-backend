using System.Security.Claims;
using FluentValidation;
using RentNearBy.Api.Handlers.Buddy;
using RentNearBy.Core.DTOs.Social;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Services.Social;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers.Social;

// Contract 2.7: POST /social/reports (trip post or message), existing report limit (5 per hour).
public static class SocialReportHandlers
{
    public static async Task<IResult> Report(
        SocialReportRequest request, IValidator<SocialReportRequest> validator, ClaimsPrincipal principal, SocialReportService reports,
        IRateLimitService limiter, HttpContext http, CancellationToken ct)
    {
        if (!BuddyHttp.TryCaller(principal, out var me)) return UnauthorizedResponse();
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid) return SocialHttp.Invalid(validation);
        return await BuddyHttp.ActionLimitAsync(limiter, http, SocialRateLimits.Report(me),
            async () => SocialHttp.From(await reports.ReportAsync(me, request, ct)));
    }
}
