using System.Security.Claims;
using FluentValidation;
using RentNearBy.Api.Handlers.Buddy;
using RentNearBy.Core.DTOs.Social;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.Social;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers.Social;

// Contract 2.6: chat REST (the hub twins are ChatHub.DmSend / DmRead).
public static class SocialChatHandlers
{
    public static Task<IResult> Rooms(
        ClaimsPrincipal principal, SocialChatService chat, IRateLimitService limiter, HttpContext http, CancellationToken ct,
        string? bucket = null, string? cursor = null, int? limit = null)
    {
        if (ChatBuckets.FromQuery(bucket) is not { } b) return Task.FromResult(BadRequestResponse("Bucket must be people, trips or requests."));
        if (!BuddyHttp.TryKeyset(cursor, out var key)) return Task.FromResult(BuddyHttp.BadCursor());
        return SocialHttp.ReadAsync(principal, limiter, http, async me => OkResponse(await chat.RoomsAsync(me, b, key, SocialSql.Clamp(limit), ct)));
    }

    public static Task<IResult> WithUser(
        Guid userId, ClaimsPrincipal principal, SocialChatService chat, IRateLimitService limiter, HttpContext http, CancellationToken ct)
        => SocialHttp.ReadAsync(principal, limiter, http, async me => SocialHttp.From(await chat.WithUserAsync(me, userId, ct)));

    public static Task<IResult> TripRoom(
        Guid tripId, ClaimsPrincipal principal, SocialChatService chat, IRateLimitService limiter, HttpContext http, CancellationToken ct)
        => SocialHttp.ReadAsync(principal, limiter, http, async me =>
            await chat.TripRoomAsync(me, tripId, ct) is { } room ? OkResponse(room) : SocialHttp.Error(SocialErrorTypes.RoomNotFound));

    public static Task<IResult> Room(
        Guid roomId, ClaimsPrincipal principal, SocialChatService chat, IRateLimitService limiter, HttpContext http, CancellationToken ct)
        => SocialHttp.ReadAsync(principal, limiter, http, async me =>
            await chat.RoomAsync(me, roomId, ct) is { } room ? OkResponse(room) : SocialHttp.Error(SocialErrorTypes.RoomNotFound));

    public static async Task<IResult> Start(
        StartChatRequest request, IValidator<StartChatRequest> validator, ClaimsPrincipal principal, SocialChatService chat,
        ApplicationDbContext db, IRateLimitService limiter, HttpContext http, CancellationToken ct)
    {
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid) return SocialHttp.Invalid(validation);
        if (!BuddyHttp.TryCaller(principal, out var me)) return UnauthorizedResponse();
        if (await SocialSql.GuardAsync(db, me, true, ct) is { } fail) return SocialHttp.Error(fail.Type, fail.Data);
        var lease = new RateLimitPermits(limiter);
        if (await BuddyHttp.TakeAsync(lease, http, SocialRateLimits.ChatNew(me)) is { } denied) return denied;
        RentNearBy.Core.Models.Buddy.BuddyResult<StartedChatDto> result;
        try
        {
            result = await chat.StartAsync(me, request.ToUserId, request.ClientMessageId, request.Text, ct);
        }
        catch
        {
            await lease.ReleaseAllAsync();
            throw;
        }
        // A start into an existing room is a plain send and opens no new chat, so the new-chat permit goes back; a refused
        // start gives it back like every other social write (BuddyHttp.ActionLimitAsync).
        var giveBack = result.Ok
            ? result.Value!.IntoExistingRoom
            : SocialErrorTypes.StatusOf(result.ErrorType!) is not (401 or 403 or 404);
        if (giveBack) await lease.ReleaseAllAsync();
        return SocialHttp.From(result);
    }

    public static Task<IResult> Messages(
        Guid roomId, ClaimsPrincipal principal, SocialChatService chat, IRateLimitService limiter, HttpContext http, CancellationToken ct,
        long? beforeSeq = null, long? afterSeq = null, int? limit = null)
        => SocialHttp.ReadAsync(principal, limiter, http, async me => SocialHttp.From(await chat.MessagesAsync(me, roomId, beforeSeq, afterSeq, limit, ct)));

    public static async Task<IResult> Send(
        Guid roomId, SendChatMessageRequest request, IValidator<SendChatMessageRequest> validator, ClaimsPrincipal principal,
        SocialChatService chat, ApplicationDbContext db, IRateLimitService limiter, HttpContext http, CancellationToken ct)
    {
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid) return SocialHttp.Invalid(validation);
        return await SocialHttp.WriteAsync(principal, db, limiter, http, SocialRateLimits.ChatSend(ProfileHandlers.BuddyCaller(principal)), false,
            async me => SocialHttp.From(await chat.SendAsync(me, roomId, request.ClientMessageId, request.Text, ct)), ct);
    }

    public static Task<IResult> Read(
        Guid roomId, ChatReadRequest request, ClaimsPrincipal principal, SocialChatService chat, ApplicationDbContext db,
        IRateLimitService limiter, HttpContext http, CancellationToken ct)
        => SocialHttp.WriteAsync(principal, db, limiter, http, SocialRateLimits.Write(ProfileHandlers.BuddyCaller(principal)), false,
            async me =>
            {
                var result = await chat.ReadAsync(me, roomId, request.Seq, ct);
                return result.Ok ? OkResponse(new ChatReadResultDto { Unread = result.Value! }) : SocialHttp.Error(result.ErrorType!, result.ErrorData);
            }, ct);

    public static Task<IResult> Delete(
        Guid roomId, ClaimsPrincipal principal, SocialChatService chat, ApplicationDbContext db, IRateLimitService limiter, HttpContext http,
        CancellationToken ct)
        => SocialHttp.WriteAsync(principal, db, limiter, http, SocialRateLimits.Write(ProfileHandlers.BuddyCaller(principal)), false,
            async me =>
            {
                var result = await chat.DeleteRoomAsync(me, roomId, ct);
                return result.Ok ? SocialHttp.Empty() : SocialHttp.Error(result.ErrorType!, result.ErrorData);
            }, ct);
}
