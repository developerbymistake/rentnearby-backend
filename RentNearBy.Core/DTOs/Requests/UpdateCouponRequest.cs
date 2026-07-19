namespace RentNearBy.Core.DTOs.Requests;

// ClearMaxTotalRedemptions/ClearValidUntil exist because the corresponding nullable fields can't
// otherwise distinguish "field omitted, leave unchanged" from "explicitly set to null" — both arrive
// as null. Default false, so existing callers that never set them behave exactly as before.
public record UpdateCouponRequest(
    int? CoinValue,
    int? MaxTotalRedemptions,
    bool ClearMaxTotalRedemptions,
    DateTime? ValidUntil,
    bool ClearValidUntil,
    string? CampaignLabel,
    string? Status);
