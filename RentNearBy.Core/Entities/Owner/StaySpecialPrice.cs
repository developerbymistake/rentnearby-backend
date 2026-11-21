namespace RentNearBy.Core.Entities.Owner;

// One special price rule over inclusive nights; applies to every room type of the stay. LabelCopy is the festival name copied at save.
public class StaySpecialPrice
{
    public Guid Id { get; set; }
    public Guid StayListingId { get; set; }
    public DateOnly DateFrom { get; set; }
    public DateOnly DateTo { get; set; }
    public string Direction { get; set; } = string.Empty;
    public short Percent { get; set; }
    public Guid? FestivalDateId { get; set; }
    public string? LabelCopy { get; set; }
    public bool IsPaused { get; set; }
    public bool PausedByPlan { get; set; }
    public int Version { get; set; } = 1;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
