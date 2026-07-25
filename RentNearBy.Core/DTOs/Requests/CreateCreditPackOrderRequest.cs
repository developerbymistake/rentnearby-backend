namespace RentNearBy.Core.DTOs.Requests;

// Confirmed lets a client re-submit after acknowledging a RECENT_PURCHASE_DETECTED warning (see
// CreditPackPurchaseService.CreateOrderAsync) — skips the recency check on the retry, nothing else.
public record CreateCreditPackOrderRequest(Guid CreditPackId, bool Confirmed = false);
