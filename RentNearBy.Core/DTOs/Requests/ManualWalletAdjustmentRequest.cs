namespace RentNearBy.Core.DTOs.Requests;

// IdempotencyKey is client-generated and held across retries of the same logical submit — this is
// what makes a double-tapped Credit/Debit button apply exactly once instead of twice.
public record ManualWalletAdjustmentRequest(int Amount, string Reason, Guid IdempotencyKey);
