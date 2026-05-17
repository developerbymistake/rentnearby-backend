namespace RentNearBy.Core.DTOs.Responses;

public class CreatePaymentOrderResponse
{
    public string OrderId { get; set; } = string.Empty;
    public double Amount { get; set; }
    public string Currency { get; set; } = "INR";
    public string KeyId { get; set; } = string.Empty;
}
