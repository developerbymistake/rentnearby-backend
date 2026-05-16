namespace RentNearBy.Core.Entities;

public class PaymentFeature
{
    public Guid Id { get; set; }
    public bool IsEnabled { get; set; } = false;
    public int FreePlanDays { get; set; } = 10;
    public int FreePlanRoomLimit { get; set; } = 1;
    public int PaidPlanPrice { get; set; } = 99;
    public int PaidPlanDays { get; set; } = 30;
    public int PaidPlanRoomLimit { get; set; } = 2;
    public int? FreeListingDaysWhenDisabled { get; set; } = null;  // null = indefinite, or set days (e.g., 365)
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
