using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace RentNearBy.Api.Hubs;

[Authorize]
public class BannerHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        var districtId = Context.GetHttpContext()?.Request.Query["districtId"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(districtId))
            await Groups.AddToGroupAsync(Context.ConnectionId, $"district_{districtId}");
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var districtId = Context.GetHttpContext()?.Request.Query["districtId"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(districtId))
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"district_{districtId}");
        await base.OnDisconnectedAsync(exception);
    }
}
