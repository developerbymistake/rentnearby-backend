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

// B12, B13, B14, B17. The chat routes call the same IUserBlockService.
public static class UserBlockHandlers
{
    public const string Blocked = "Blocked";
    public const string Active = "Active";

    public static async Task<IResult> Block(
        Guid userId, BlockPersonRequest? request, IValidator<BlockPersonRequest> validator, ClaimsPrincipal principal,
        IUserBlockService blocks, UserReportService reports, IHubContext<ChatHub> hub, IRateLimitService limiter, HttpContext http,
        CancellationToken ct, [Microsoft.AspNetCore.Mvc.FromServices] RentNearBy.Infrastructure.Services.Social.DiscoverCache? discover = null)
    {
        if (!BuddyHttp.TryCaller(principal, out var me)) return UnauthorizedResponse();
        request ??= new BlockPersonRequest();
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid) return BuddyHttp.Invalid(validation);
        if (userId == me) return BuddyHttp.Error(BuddyErrorTypes.CannotBlockSelf);

        return await BuddyHttp.ActionLimitAsync(limiter, http, BuddyRateLimitKeys.Block(me), async () =>
        {
            if (request.AlsoReport is { } report
                && await reports.ReportAsync(me, userId, report.ReasonId, report.Note, request.Context, ct) == ReportFiled.InvalidReason)
                return BuddyHttp.Error(BuddyErrorTypes.InvalidReason);
            var outcome = await blocks.BlockAsync(me, userId, ct);
            if (outcome.Result == BlockResult.TooManyBlocks) return BuddyHttp.Error(BuddyErrorTypes.TooManyBlocks);
            await PushConversationStatusAsync(hub, outcome.FlippedConversationIds, Blocked, userId, me);
            discover?.Invalidate(me);
            return NoContentResponse();
        });
    }

    public static async Task<IResult> Unblock(
        Guid userId, ClaimsPrincipal principal, IUserBlockService blocks, IHubContext<ChatHub> hub, IRateLimitService limiter,
        HttpContext http, CancellationToken ct)
    {
        if (!BuddyHttp.TryCaller(principal, out var me)) return UnauthorizedResponse();
        if (userId == me) return NoContentResponse();
        return await BuddyHttp.ActionLimitAsync(limiter, http, BuddyRateLimitKeys.Block(me), async () =>
        {
            var outcome = await blocks.UnblockAsync(me, userId, ct);
            await PushConversationStatusAsync(hub, outcome.FlippedConversationIds, Active, userId, null);
            return NoContentResponse();
        });
    }

    public static async Task<IResult> MyBlocks(
        ClaimsPrincipal principal, BuddyListService lists, IRateLimitService limiter, HttpContext http, CancellationToken ct,
        string? cursor = null, int? limit = null)
    {
        if (!BuddyHttp.TryCaller(principal, out var me)) return UnauthorizedResponse();
        if (!BuddyHttp.TryKeyset(cursor, out var key)) return BuddyHttp.BadCursor();
        if (await BuddyHttp.ReadLimitAsync(limiter, http, BuddyRateLimitKeys.BuddyRead(me)) is { } denied) return denied;
        var take = BuddyHttp.ClampLimit(limit, BuddyLimits.BlocksPageMax, BuddyLimits.BlocksPageMax);
        return OkResponse(await lists.BlocksAsync(me, key, take, ct));
    }

    public static async Task<IResult> BlockContext(
        Guid userId, ClaimsPrincipal principal, BuddyListService lists, IRateLimitService limiter, HttpContext http, CancellationToken ct)
    {
        if (!BuddyHttp.TryCaller(principal, out var me)) return UnauthorizedResponse();
        if (await BuddyHttp.ReadLimitAsync(limiter, http, BuddyRateLimitKeys.BuddyRead(me)) is { } denied) return denied;
        var context = await lists.BlockContextAsync(me, userId, ct);
        return context is null ? BuddyHttp.Error(BuddyErrorTypes.PersonNotFound) : OkResponse(context);
    }

    // Live status to the open thread and to the other person's chat list (same events as before; blockedByUserId on a block only).
    public static Task PushConversationStatusAsync(
        IHubContext<ChatHub> hub, IReadOnlyList<Guid> conversationIds, string status, Guid otherUserId, Guid? blockedByUserId)
    {
        if (conversationIds.Count == 0) return Task.CompletedTask;
        var sends = conversationIds.SelectMany(id =>
        {
            object payload = blockedByUserId is { } by
                ? new { conversationId = id, status, blockedByUserId = by }
                : new { conversationId = id, status };
            return new[]
            {
                hub.Clients.Group($"conversation_{id}").SendAsync("ConversationStatusChanged", payload),
                hub.Clients.Group($"user_{otherUserId}").SendAsync("ConversationStatusChanged", payload),
            };
        });
        return Task.WhenAll(sends);
    }
}
