namespace RentNearBy.Core.Entities;

public class PlanPrice
{
    public Guid Id { get; set; }
    public Guid PlanId { get; set; }
    public short CycleMonths { get; set; }
    public int PriceInr { get; set; }
    // Reserved for the designed (not built) AutoPay subscription layer.
    public string? RazorpayPlanId { get; set; }
    public bool IsEnabled { get; set; } = true;
}
