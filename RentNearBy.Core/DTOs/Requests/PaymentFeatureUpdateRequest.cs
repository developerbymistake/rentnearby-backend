namespace RentNearBy.Core.DTOs.Requests;

public class PaymentFeatureUpdateRequest
{
    public bool IsEnabled { get; set; }
    public int? FreePlanDays { get; set; }
    public int? FreePlanRoomLimit { get; set; }
    public int? PaidPlanPrice { get; set; }
    public int? PaidPlanDays { get; set; }
    public int? PaidPlanRoomLimit { get; set; }
}
