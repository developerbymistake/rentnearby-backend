using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using RentNearBy.Api.Handlers;
using RentNearBy.Core.Interfaces;

namespace RentNearBy.Api.Hubs;

// Independent of BannerHub — no shared code, mirrors its shape only.
[Authorize]
public class ChatHub : Hub
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
            await Groups.AddToGroupAsync(Context.ConnectionId, $"user_{userId}");

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (UsersHandlers.TryGetUserId(Context.User!, out var userId))
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"user_{userId}");

        await base.OnDisconnectedAsync(exception);
    }

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
