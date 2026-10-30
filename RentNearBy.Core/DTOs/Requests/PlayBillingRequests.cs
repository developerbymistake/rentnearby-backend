namespace RentNearBy.Core.DTOs.Requests;

// POST /plans/purchases/{id}/verify-play. ProductId is only used for the ack after the snapshot confirmed it.
public record VerifyPlayPurchaseRequest(string PurchaseToken, string ProductId);

// POST /plans/purchases/restore (1..PlayRules.RestoreMaxTokens items).
public record RestorePlayPurchasesRequest(List<RestorePlayItem> Purchases);

public record RestorePlayItem(string PurchaseToken, string ProductId);
