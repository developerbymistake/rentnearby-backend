namespace RentNearBy.Core.Models;

// Shared between CreditPackPurchaseService (throw sites) and CreditPackHandlers (catch site, maps each
// message to a machine-readable ApiError.Type) — one place to keep them in sync, no duplicated string
// literals to drift apart.
public static class CreditPackPurchaseErrors
{
    public const string AlreadyProcessed = "Already processed.";
    public const string PreviouslyFailed = "Purchase previously failed. Please start a new one.";
    public const string SignatureInvalid = "Payment verification failed.";
    public const string RecentPurchaseDetected = "You recently bought credits with this account. Buy again?";
}
