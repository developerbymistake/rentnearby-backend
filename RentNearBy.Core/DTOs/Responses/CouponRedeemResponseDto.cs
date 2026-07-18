namespace RentNearBy.Core.DTOs.Responses;

public class CouponRedeemResponseDto
{
    public int CoinsCredited { get; set; }
    public int NewBalance { get; set; }
    public string? CampaignLabel { get; set; }
}
