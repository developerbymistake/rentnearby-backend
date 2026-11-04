namespace RentNearBy.Core.Entities;

// One row per sent trip push that must go out once (DedupeKey unique, e.g. "exp:{enquiryId}").
public class TripPushLog
{
    public Guid Id { get; set; }
    public Guid? TripId { get; set; }
    public Guid? TripEnquiryId { get; set; }
    public string DedupeKey { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public DateTime SentAt { get; set; }
}
