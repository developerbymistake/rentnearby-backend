namespace RentNearBy.Core.DTOs.Requests;

// Step 2 — "is this your number?" confirmation. Carries only the opaque Step-1 token; the phone number
// itself is never re-read from the client here or in Step 3 — it lives solely in the server-side state
// blob the token points at, so a confirmed/verified number can never be swapped mid-flow by a caller
// re-sending a different Mobile value.
public record WebEnquiryConfirmRequest(string Token);
