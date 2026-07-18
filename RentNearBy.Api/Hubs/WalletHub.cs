using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using RentNearBy.Api.Handlers;

namespace RentNearBy.Api.Hubs;

// Independent of ChatHub/BannerHub — no shared code, mirrors ChatHub's always-join-user_{id} shape
// only. Push-only: no client-invokable methods, no sub-group concept (a wallet balance is a pure
// per-user value, never scoped to anything narrower).
[Authorize]
public class WalletHub : Hub
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
