namespace RentNearBy.Core.Entities;

public class ReportReason
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int SortOrder { get; set; } = 999;
    public string Scope { get; set; } = "Listing";
    public DateTime CreatedAt { get; set; }

    public ICollection<ListingReport> Reports { get; set; } = new List<ListingReport>();
}
