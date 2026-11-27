namespace RentNearBy.Core.Entities;

// Which phone document got which number. No guest data. WindowAt moves only on a counted download.
public class StayQuoteIssue
{
    public Guid UserId { get; set; }
    public Guid DocumentId { get; set; }
    public short Kind { get; set; }
    public int Number { get; set; }
    public DateTime IssuedAt { get; set; }
    public DateTime WindowAt { get; set; }
    public short Downloads { get; set; }
    public short FreeUses { get; set; }
}
