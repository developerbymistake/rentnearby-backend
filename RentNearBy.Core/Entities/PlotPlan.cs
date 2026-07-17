namespace RentNearBy.Core.Entities;

public class PlotPlan
{
    public Guid Id { get; set; }
    public string PlanType { get; set; } = string.Empty;
    public int Days { get; set; }
    public int PlotListingLimit { get; set; }
    // Both fields are coin amounts, not rupees — see the matching comment on RoomPlan.
    public int Price { get; set; }          // Sticker coin amount (MRP, shown with strikethrough)
    public int DiscountPercent { get; set; } = 0;
    public int OriginalPrice { get; set; } = 0; // Actual coin cost to Go Live on this plan
    public bool IsEnabled { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
