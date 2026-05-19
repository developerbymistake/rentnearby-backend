namespace RentNearBy.Core.Entities;

public class PlotMembership
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string PlanType { get; set; } = string.Empty;
    public DateTime ValidFrom { get; set; }
    public DateTime ValidUntil { get; set; }
    public int MaxPlots { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public User User { get; set; } = null!;
}
