using Microsoft.AspNetCore.SignalR;
using RentNearBy.Api.Hubs;
using RentNearBy.Core.Interfaces;

namespace RentNearBy.Api.Services;

// Api-side implementation of Core's IWalletNotifier — the only place in the codebase allowed to know
// about WalletHub directly, so Infrastructure-layer callers (PendingCoinPurchaseCleanupService) can
// push a balance update without a project reference to Api.
public class WalletNotifier(IHubContext<WalletHub> hubContext, ILogger<WalletNotifier> logger) : IWalletNotifier
{
    public async Task NotifyBalanceChangedAsync(Guid userId, int newBalance, string reason)
    {
        try
        {
            await hubContext.Clients.Group($"user_{userId}").SendAsync("WalletBalanceChanged", new
            {
                balance = newBalance,
                reason,
                occurredAt = DateTime.UtcNow,
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "WalletNotifier: push failed for user {UserId}", userId);
        }
    }
}
