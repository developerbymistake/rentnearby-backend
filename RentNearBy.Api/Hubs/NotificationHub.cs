using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using RentNearBy.Api.Extensions;
using RentNearBy.Api.Handlers;
using RentNearBy.Api.Services;
using RentNearBy.Core.Entities.TaxiRequests;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Api.Hubs;

// Independent of ChatHub/BannerHub — no shared code, always joins the user_{id} group. Push events are scoped to the owning user; the only client-invokable
// methods are the taxi request subscriptions, which join a per-request group only for that request's traveller.
[Authorize]
public class NotificationHub : Hub
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

    // Contract section 9: a seeker caller and ownership are the rules; reads stay open while the master switch is off, so hints keep flowing.
    public async Task SubscribeTaxiRequest(string requestId, ApplicationDbContext db)
    {
        if (!Guid.TryParse(requestId, out var id) || !UsersHandlers.TryGetUserId(Context.User!, out var userId)) return;
        if (!SeekerGate.IsSeeker(Context.User!)) return;
        var aborted = Context.ConnectionAborted;
        await TaxiRequestSubscriptions.JoinIfTravellerAsync(Groups, Context.ConnectionId, userId, id,
            () => db.Set<TaxiRequest>().AsNoTracking().Where(r => r.Id == id).Select(r => (Guid?)r.TravellerUserId).FirstOrDefaultAsync(aborted));
    }

    public Task UnsubscribeTaxiRequest(string requestId)
        => Guid.TryParse(requestId, out var id)
            ? Groups.RemoveFromGroupAsync(Context.ConnectionId, TaxiRequestRoutes.HubGroup(id))
            : Task.CompletedTask;
}
