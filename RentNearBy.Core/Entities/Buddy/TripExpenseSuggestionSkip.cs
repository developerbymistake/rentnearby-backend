namespace RentNearBy.Core.Entities.Buddy;

public class TripExpenseSuggestionSkip
{
    public Guid TripId { get; set; }
    public string SourceKind { get; set; } = string.Empty;
    public Guid SourceId { get; set; }
    public DateTime SkippedAt { get; set; }
}
