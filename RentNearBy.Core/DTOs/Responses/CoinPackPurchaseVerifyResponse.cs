namespace RentNearBy.Core.DTOs.Responses;

public class CoinPackPurchaseVerifyResponse
{
    public bool Success { get; set; }
    public int CoinsCredited { get; set; }
    public int NewBalance { get; set; }
}
