namespace RentNearBy.Core.Entities.Owner;

// One owner calendar row (contract 1.1). Auto rows follow a confirmed booking through the sync; Manual rows are typed by the owner.
// Money columns are owner-only: no traveller DTO, admin DTO, push or log line may carry them.
public class CalendarEntry
{
    public Guid Id { get; set; }
    public Guid OwnerUserId { get; set; }
    public string ListingKind { get; set; } = string.Empty;
    public Guid ListingId { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string? SourceType { get; set; }
    public Guid? SourceId { get; set; }
    public Guid? TravellerUserId { get; set; }
    public DateOnly DateFrom { get; set; }
    public DateOnly DateTo { get; set; }
    public TimeOnly? TimeOfDay { get; set; }
    public string? FromPlace { get; set; }
    public string? ToPlace { get; set; }
    public string Label { get; set; } = string.Empty;
    public string? Detail { get; set; }
    public string? Note { get; set; }
    public int? ListedTotal { get; set; }
    public int? AgreedTotal { get; set; }
    public short? Guests { get; set; }
    public short? Seats { get; set; }
    public string? RoomNames { get; set; }
    public string? PlacesJson { get; set; }
    public string? CancelledBy { get; set; }
    public DateOnly? CancelledOn { get; set; }
    public int? FinalAmount { get; set; }
    public int? AdvanceReceived { get; set; }
    public bool PaidInFull { get; set; }
    public bool Cancelled { get; set; }
    // Taxi manual rows only: true = the driver still takes app bookings that day (the row does not hide the taxi).
    public bool KeepVisible { get; set; }
    public int Version { get; set; } = 1;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
