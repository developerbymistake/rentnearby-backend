using Microsoft.AspNetCore.SignalR;
using RentNearBy.Api.Hubs;
using RentNearBy.Core.DTOs.TaxiRequests;
using RentNearBy.Core.Interfaces.TaxiRequests;
using RentNearBy.Core.Models.TaxiRequests;

namespace RentNearBy.Api.Services;

// Realtime hint to the traveller's Status screen (contract section 9). Never throws: the commit it follows has already happened.
public sealed class TaxiRequestEvents(IHubContext<NotificationHub> hub, ILogger<TaxiRequestEvents> logger) : ITaxiRequestEvents
{
    public async Task RequestChangedAsync(Guid requestId, Guid tripId, Guid travellerUserId, long version, string state)
    {
        try
        {
            var payload = new TaxiRequestChangedEvent { RequestId = requestId, TripId = tripId, Version = version, State = state };
            await hub.Clients.Group(TaxiRequestRoutes.HubGroup(requestId)).SendAsync(TaxiRequestRoutes.HubEvent, payload);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "taxi.request.event.failed {RequestId}", requestId);
        }
    }
}

// Only the request's own traveller may join its group.
public static class TaxiRequestSubscriptions
{
    public static bool CanJoin(Guid callerUserId, Guid? requestTravellerUserId)
        => requestTravellerUserId is { } owner && owner == callerUserId;

    public static async Task<bool> JoinIfTravellerAsync(IGroupManager groups, string connectionId, Guid callerUserId, Guid requestId, Func<Task<Guid?>> findTraveller)
    {
        if (!CanJoin(callerUserId, await findTraveller())) return false;
        await groups.AddToGroupAsync(connectionId, TaxiRequestRoutes.HubGroup(requestId));
        return true;
    }
}
