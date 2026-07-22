namespace RentNearBy.Core.Interfaces;

// Lets Infrastructure-layer code (which has no project reference to Api, where WalletHub/
// IHubContext<WalletHub> live) push a real-time wallet balance update without depending on Api
// directly — implemented in RentNearBy.Api/Services/WalletNotifier.cs.
public interface IWalletNotifier
{
    Task NotifyBalanceChangedAsync(Guid userId, int newBalance, string reason);
}
