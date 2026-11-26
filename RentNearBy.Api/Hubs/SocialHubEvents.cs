using Microsoft.AspNetCore.SignalR;
using RentNearBy.Core.Interfaces.Social;
using RentNearBy.Core.Models.Social;

namespace RentNearBy.Api.Hubs;

// Sends the social chat events after commit: always to the dm_{userId} group, one send per member. Best effort, never throws.
public sealed class SocialHubEvents(IHubContext<ChatHub> hub, ILogger<SocialHubEvents> logger) : ISocialRealtime
{
    public async Task SendAsync(IReadOnlyCollection<SocialHubEvent> events)
    {
        foreach (var e in events) // batch-loop: one SignalR send per member and event (a trip room has at most 12 members)
        {
            try
            {
                await hub.Clients.Group(SocialHubEventNames.Group(e.UserId)).SendAsync(e.Event, e.Payload);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "social.hub.send_failed {Event}", e.Event);
            }
        }
    }
}

// Contract 3.1: ends the ChatHub sockets of a user after a logout, role change, admin pause or account deletion.
public sealed class HubSessionRevoker(HubConnectionRegistry registry, IHubContext<ChatHub> hub, ILogger<HubSessionRevoker> logger) : IHubSessionRevoker
{
    public async Task RevokeAsync(Guid userId, string reason)
    {
        foreach (var context in registry.Of(userId)) // batch-loop: one call per live connection of this user
        {
            try
            {
                await hub.Clients.Client(context.ConnectionId).SendAsync(SocialHubEventNames.SessionEnded, new { reason });
                context.Abort();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "social.hub.revoke_failed {UserId}", userId);
            }
        }
    }
}
