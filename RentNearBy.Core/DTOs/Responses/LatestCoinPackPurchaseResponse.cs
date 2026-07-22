namespace RentNearBy.Core.DTOs.Responses;

// Deliberately no balance field — wallet balance stays single-sourced from GET /wallet/balance, never
// duplicated here. Used by the client as a last-resort status check after a verify-payment call whose
// response was lost (network drop, or the app was killed and relaunched with no in-memory order state
// left) — see PendingCoinPurchaseCleanupService/CoinPackPurchaseService.ReconcileWithRazorpayAsync for
// the equivalent server-side safety net.
public class LatestCoinPackPurchaseResponse
{
    public bool HasPurchase { get; set; }
    public string? Status { get; set; }
    public string? FailureReason { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}
