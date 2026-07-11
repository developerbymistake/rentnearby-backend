using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using RentNearBy.Api.Handlers;

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
        if (UsersHandlers.TryGetUserId(Context.User!, out var userId))
            await Groups.AddToGroupAsync(Context.ConnectionId, $"user_{userId}");

        // Joined only while a specific conversation screen is actually open.
        var conversationId = Context.GetHttpContext()?.Request.Query["conversationId"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(conversationId))
            await Groups.AddToGroupAsync(Context.ConnectionId, $"conversation_{conversationId}");

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (UsersHandlers.TryGetUserId(Context.User!, out var userId))
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"user_{userId}");

        var conversationId = Context.GetHttpContext()?.Request.Query["conversationId"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(conversationId))
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"conversation_{conversationId}");

        await base.OnDisconnectedAsync(exception);
    }
}
