namespace RentNearBy.Core.Entities;

public class PlotPlan
{
    public Guid Id { get; set; }
    public string PlanType { get; set; } = string.Empty;
    public int Days { get; set; }
    public int PlotLimit { get; set; }
    public int Price { get; set; }
    public int OriginalPrice { get; set; } = 0;
    public int DiscountPercent { get; set; } = 0;
    public bool IsEnabled { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
