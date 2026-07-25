namespace RentNearBy.Core.DTOs.Responses;

public class CreditPackPurchaseVerifyResponse
{
    public bool Success { get; set; }
    public int CreditsCredited { get; set; }
    public int NewBalance { get; set; }
}
