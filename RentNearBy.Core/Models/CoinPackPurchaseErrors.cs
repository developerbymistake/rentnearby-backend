namespace RentNearBy.Core.Models;

// Shared between CoinPackPurchaseService (throw sites) and CoinPackHandlers (catch site, maps each
// message to a machine-readable ApiError.Type) — one place to keep them in sync, no duplicated string
// literals to drift apart.
public static class CoinPackPurchaseErrors
{
    public const string AlreadyProcessed = "Already processed.";
    public const string PreviouslyFailed = "Purchase previously failed. Please start a new one.";
    public const string SignatureInvalid = "Payment verification failed.";
    public const string RecentPurchaseDetected = "You recently bought coins with this account. Buy again?";
}
