namespace RentNearBy.Core.DTOs.Responses;

public class CouponDto
{
    public Guid Id { get; set; }
    public string? Code { get; set; }
    public int CoinValue { get; set; }
    public string TriggerType { get; set; } = string.Empty;
    public int PerUserLimit { get; set; }
    public int? MaxTotalRedemptions { get; set; }
    public int CurrentRedemptions { get; set; }
    public DateTime ValidFrom { get; set; }
    public DateTime? ValidUntil { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? CampaignLabel { get; set; }
    public DateTime CreatedAt { get; set; }
}
