namespace RentNearBy.Core.Entities;

// Phase 2 (Today / Problem); schema only.
public class TripProblemLog
{
    public Guid Id { get; set; }
    public Guid TripId { get; set; }
    public Guid? TripEnquiryId { get; set; }
    public short Day { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string? Note { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public DateTime CreatedAt { get; set; }
}
