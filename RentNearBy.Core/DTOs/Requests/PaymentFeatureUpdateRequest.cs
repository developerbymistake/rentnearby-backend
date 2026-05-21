namespace RentNearBy.Core.DTOs.Requests;

public class PaymentFeatureUpdateRequest
{
    public bool IsEnabled { get; set; }
    public int? FreeLimit { get; set; }
    public int? FreeDays { get; set; }
}
