namespace RentNearBy.Core.DTOs.Requests;

// PerUserLimit intentionally absent — hardcoded server-side to 1 (see design doc §7 Open Flags).
public record CreateCouponRequest(string TriggerType, int CoinValue, int? MaxTotalRedemptions, DateTime ValidFrom, DateTime? ValidUntil, string? CampaignLabel);
