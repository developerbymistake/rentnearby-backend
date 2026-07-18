using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using RentNearBy.Api.Handlers;

namespace RentNearBy.Api.Hubs;

// Independent of WalletHub/ChatHub/BannerHub — no shared code, mirrors WalletHub's always-join-
// user_{id} shape only. Push-only: no client-invokable methods, no sub-group concept (an inquiry's
// status is scoped to the owning user, never to anything narrower).
[Authorize]
public class InquiryHub : Hub
{
    public override async Task OnConnectedAsync()
    {
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
}
