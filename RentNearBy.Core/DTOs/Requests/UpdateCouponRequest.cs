namespace RentNearBy.Core.DTOs.Requests;

public record UpdateCouponRequest(int? CoinValue, int? MaxTotalRedemptions, DateTime? ValidUntil, string? CampaignLabel, string? Status);
