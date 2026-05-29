namespace RentNearBy.Core.Entities;

public class Plan
{
    public Guid Id { get; set; }
    public string PlanType { get; set; } // "FREE" or "PAID"
    public int Days { get; set; }
    public int RoomLimit { get; set; }
    public int Price { get; set; } // in INR
    public int OriginalPrice { get; set; } = 0;
    public int DiscountPercent { get; set; } = 0;
    public bool IsEnabled { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
