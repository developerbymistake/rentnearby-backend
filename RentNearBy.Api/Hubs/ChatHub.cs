using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using RentNearBy.Api.Extensions;
using RentNearBy.Api.Handlers;
using RentNearBy.Core.DTOs.Social;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Interfaces.Social;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Core.Models.Social;

namespace RentNearBy.Api.Hubs;

// Independent of BannerHub — no shared code, mirrors its shape only. Social chat (contract 3): seekers also join dm_{userId};
// DmSend / DmRead are guarded by SocialHubFilter and re-check the live seeker role in every statement.
[Authorize]
public class ChatHub(HubConnectionRegistry registry) : Hub
{
    public override async Task OnConnectedAsync()
    {
        // Always joins the caller's own group — this is what lets the Chats-tab
        // unread badge and "new message" notifications update live even when no
        // specific conversation thread is open (e.g. while browsing listings).
        // Conversation-group membership is no longer connect-time/query-string based —
        // see JoinConversation/LeaveConversation, which let a client move between
        // conversations on this same connection instead of reconnecting per screen.
        if (UsersHandlers.TryGetUserId(Context.User!, out var userId))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"user_{userId}");
            registry.Add(userId, Context);
            if (SeekerGate.IsSeeker(Context.User!))
                await Groups.AddToGroupAsync(Context.ConnectionId, SocialHubEventNames.Group(userId));
        }

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (UsersHandlers.TryGetUserId(Context.User!, out var userId))
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"user_{userId}");
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, SocialHubEventNames.Group(userId));
            registry.Remove(userId, Context.ConnectionId);
        }

        await base.OnDisconnectedAsync(exception);
    }

    // Social 1-to-1 / trip chat send (REST twin: POST /social/chat/rooms/{roomId}/messages).
    public async Task<DmSendResultDto> DmSend(string roomId, string clientMessageId, string text, ISocialChatService chat, IRateLimitService limiter)
    {
        if (!UsersHandlers.TryGetUserId(Context.User!, out var me)) return Fail(SocialErrorTypes.RoomNotFound);
        if (!Guid.TryParse(roomId, out var room)) return Fail(SocialErrorTypes.RoomNotFound);
        if (!Guid.TryParse(clientMessageId, out var client) || client == Guid.Empty) return Fail(SocialErrorTypes.RoomNotFound);
        // Same permit rule as the REST twin (BuddyHttp.ActionLimitAsync): a send that wrote nothing gives its permit back.
        var limit = SocialRateLimits.ChatSend(me);
        var lease = new RateLimitPermits(limiter);
        if (!(await lease.TakeAsync(limit.Key, limit.Max, limit.Window)).IsAllowed) return Fail(SocialErrorTypes.TooManyRequests);
        BuddyResult<ChatMessageDto> result;
        try
        {
            result = await chat.SendAsync(me, room, client, text ?? string.Empty, Context.ConnectionAborted);
        }
        catch
        {
            await lease.ReleaseAllAsync();
            throw;
        }
        if (result.Ok) return new DmSendResultDto { Ok = true, Message = result.Value };
        if (SocialErrorTypes.StatusOf(result.ErrorType!) is not (401 or 403 or 404)) await lease.ReleaseAllAsync();
        return Fail(result.ErrorType!, result.ErrorData);
    }

    public async Task DmRead(string roomId, long seq, ISocialChatService chat)
    {
        if (!UsersHandlers.TryGetUserId(Context.User!, out var me) || !Guid.TryParse(roomId, out var room)) return;
        await chat.ReadAsync(me, room, seq, Context.ConnectionAborted);
    }

    private static DmSendResultDto Fail(string type, object? data = null)
        => new() { Ok = false, Type = type, Message = SocialErrorTypes.MessageOf(type), Data = data };

    // Verifies the caller actually owns this conversation (renter or owner) before adding them to
    // its broadcast group — the old connect-time query-string mechanism this replaces had no such
    // check at all, so this closes a real gap rather than just matching prior behavior.
    public async Task JoinConversation(string conversationId, IUnitOfWork unitOfWork)
    {
        if (!UsersHandlers.TryGetUserId(Context.User!, out var userId)) return;
        if (!Guid.TryParse(conversationId, out var id)) return;

        var conversation = await unitOfWork.Conversations.GetByIdAsync(id);
        if (conversation == null || (conversation.RenterId != userId && conversation.OwnerId != userId)) return;

        await Groups.AddToGroupAsync(Context.ConnectionId, $"conversation_{conversationId}");
    }

    public async Task LeaveConversation(string conversationId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"conversation_{conversationId}");
    }
}
