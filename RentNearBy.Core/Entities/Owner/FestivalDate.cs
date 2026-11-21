namespace RentNearBy.Core.Entities.Owner;

// Admin-typed festival row. StateName null = all India (there is no States table; matched to Districts.StateName).
public class FestivalDate
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateOnly DateFrom { get; set; }
    public DateOnly DateTo { get; set; }
    public string? StateName { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
