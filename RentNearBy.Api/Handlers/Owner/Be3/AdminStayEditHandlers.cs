using RentNearBy.Core.DTOs.Owner;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Services.Owner.Be3;
using StackExchange.Redis;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers.Owner.Be3;

// A3: list, approve and reject a live stay's pending change. Both decisions carry pendingEditAt; a stay that left Approved
// answers 409 StayEditGone (the change is dropped), a newer owner save 409 StayEditChanged.
public static class AdminStayEditHandlers
{
    public static async Task<IResult> List(string? cursor, StayPendingEditService service, HttpContext http)
    {
        if (!OwnerHttp.TryTimeCursor(cursor, out var after)) return OwnerHttp.BadCursor();
        return OkResponse(await service.ListAsync(after, http.RequestAborted));
    }

    public static async Task<IResult> Approve(Guid id, AdminStayEditDecisionRequest request, StayPendingEditService service, HttpContext http, IServiceProvider sp)
    {
        if (request.PendingEditAt is not { } at) return BadRequestResponse("pendingEditAt is required");
        var decision = await service.ApproveAsync(id, at, http.RequestAborted);
        if (decision.Error == null) AdminActionLog.Write(http, "stayedit.approve", id);
        return await AnswerAsync(decision, sp);
    }

    public static async Task<IResult> Reject(Guid id, AdminStayEditDecisionRequest request, StayPendingEditService service, HttpContext http, IServiceProvider sp)
    {
        if (request.PendingEditAt is not { } at) return BadRequestResponse("pendingEditAt is required");
        var reason = request.Reason?.Trim() ?? string.Empty;
        if (reason.Length is < 1 or > OwnerLimits.PendingEditReasonMax) return BadRequestResponse("Write a reason (1 to 300 characters).");
        var decision = await service.RejectAsync(id, at, reason, http.RequestAborted);
        if (decision.Error == null) AdminActionLog.Write(http, "stayedit.reject", id);
        return await AnswerAsync(decision, sp);
    }

    private static async Task<IResult> AnswerAsync(StayEditDecision decision, IServiceProvider sp)
    {
        if (decision.Error != null) return OwnerHttp.Refuse(decision.Error);
        var redis = sp.GetService<IConnectionMultiplexer>();
        await StayCache.InvalidateAsync(redis, decision.NewDistrictId);
        if (decision.OldDistrictId != decision.NewDistrictId) await StayCache.InvalidateAsync(redis, decision.OldDistrictId);
        return OkResponse(new { decided = true });
    }
}
