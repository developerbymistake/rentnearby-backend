using System.Security.Claims;
using FluentValidation;
using Microsoft.AspNetCore.SignalR;
using RentNearBy.Api.Hubs;
using RentNearBy.Core.DTOs.Buddy;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Interfaces.Buddy;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Infrastructure.Services.Buddy.Be1;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers.Buddy.Be1;

// B15, B16. Nothing leaks: an unknown person and a repeat both answer 201.
public static class UserReportHandlers
{
    public static async Task<IResult> Report(
        Guid userId, ReportPersonRequest request, IValidator<ReportPersonRequest> validator, ClaimsPrincipal principal,
        UserReportService reports, IUserBlockService blocks, IHubContext<ChatHub> hub, IRateLimitService limiter, HttpContext http,
        RentNearBy.Infrastructure.Services.Social.SocialReportService socialReports, CancellationToken ct)
    {
        if (!BuddyHttp.TryCaller(principal, out var me)) return UnauthorizedResponse();
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid) return BuddyHttp.Invalid(validation);
        if (userId == me) return BuddyHttp.Error(BuddyErrorTypes.CannotReportSelf);

        return await BuddyHttp.ActionLimitAsync(limiter, http, BuddyRateLimitKeys.Report(me), async () =>
        {
            if (await reports.ReportAsync(me, userId, request.ReasonId, request.Note, request.Context, ct) == ReportFiled.InvalidReason)
                return BuddyHttp.Error(BuddyErrorTypes.InvalidReason);
            // A person reported from a chat: the newest messages of their direct room go with the report (contract 2.7).
            if (request.Context == UserReportContexts.Chat) await socialReports.AttachChatSnapshotAsync(me, userId, ct);
            if (request.AlsoBlock)
            {
                var outcome = await blocks.BlockAsync(me, userId, ct);
                await UserBlockHandlers.PushConversationStatusAsync(hub, outcome.FlippedConversationIds, UserBlockHandlers.Blocked, userId, me);
            }
            return CreatedResponse(new { reported = true }, $"/api/v1/users/{userId}");
        });
    }

    public static async Task<IResult> Reasons(
        ClaimsPrincipal principal, UserReportService reports, IRateLimitService limiter, HttpContext http, CancellationToken ct,
        string? scope = null)
    {
        if (!BuddyHttp.TryCaller(principal, out var me)) return UnauthorizedResponse();
        if (scope != ReportScopes.User) return BadRequestResponse("Scope must be User");
        if (await BuddyHttp.ReadLimitAsync(limiter, http, BuddyRateLimitKeys.BuddyRead(me)) is { } denied) return denied;
        return OkResponse(await reports.ReasonsAsync(ct));
    }
}
