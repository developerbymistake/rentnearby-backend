using Microsoft.AspNetCore.SignalR;
using RentNearBy.Api.Hubs;
using RentNearBy.Core.Interfaces.Buddy;

namespace RentNearBy.Api.Services;

// Realtime hints over the notification hub (groups user_{id}). Best effort, never throws: the commit already happened.
public sealed class BuddyLiveHints(IHubContext<NotificationHub> hub, ILogger<BuddyLiveHints> logger) : IBuddyLiveHints
{
    public const string TripViewChanged = "TripViewChanged";
    public const string TripExpensesChanged = "TripExpensesChanged";
    public const string BuddiesChanged = "BuddiesChanged";
    public const string SocialChanged = "SocialChanged";

    public Task TripViewChangedAsync(Guid tripId, IReadOnlyCollection<Guid> userIds)
        => SendAsync(userIds, TripViewChanged, new { tripId });

    public Task TripExpensesChangedAsync(Guid tripId, long balancesVersion, IReadOnlyCollection<Guid> userIds)
        => SendAsync(userIds, TripExpensesChanged, new { tripId, balancesVersion });

    public Task BuddiesChangedAsync(IReadOnlyCollection<Guid> userIds)
        => SendAsync(userIds, BuddiesChanged, new { });

    public Task SocialChangedAsync(string scope, IReadOnlyCollection<Guid> userIds)
        => SendAsync(userIds, SocialChanged, new { scope });

    private async Task SendAsync(IReadOnlyCollection<Guid> userIds, string eventName, object payload)
    {
        if (userIds.Count == 0) return;
        try
        {
            var groups = userIds.Distinct().Select(id => $"user_{id}").ToList();
            await hub.Clients.Groups(groups).SendAsync(eventName, payload);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "buddy.hint.failed {Event}", eventName);
        }
    }
}
