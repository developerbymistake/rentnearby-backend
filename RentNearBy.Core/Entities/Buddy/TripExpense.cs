namespace RentNearBy.Core.Entities.Buddy;

public class TripExpense
{
    public Guid Id { get; set; }
    public Guid TripId { get; set; }
    public int Amount { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public short DayNumber { get; set; }
    public string? LinkKind { get; set; }
    public Guid? LinkId { get; set; }
    public Guid PaidByUserId { get; set; }
    public string SplitMode { get; set; } = string.Empty;
    public Guid? LeftoverUserId { get; set; }
    public string? Note { get; set; }
    public int Version { get; set; }
    public Guid CreatedByUserId { get; set; }
    public Guid UpdatedByUserId { get; set; }
    public Guid CreatedOpId { get; set; }
    public Guid LastOpId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
    public Guid? DeletedByUserId { get; set; }
}
