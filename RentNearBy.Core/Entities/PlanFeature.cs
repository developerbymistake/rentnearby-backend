namespace RentNearBy.Core.Entities;

public class PlanFeature
{
    public Guid Id { get; set; }
    public Guid PlanId { get; set; }
    public string Key { get; set; } = string.Empty; // RentNearBy.Core.Models.PlanFeatureKeys.*
    public int Value { get; set; }
}
