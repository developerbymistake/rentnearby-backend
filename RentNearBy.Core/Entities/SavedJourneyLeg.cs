namespace RentNearBy.Core.Entities;

// DriverTaxiId and the backups have no FK: a role change hard-deletes taxi rows (ListingPurgeService).
public class SavedJourneyLeg
{
    public Guid Id { get; set; }
    public Guid JourneyId { get; set; }
    public short LegIndex { get; set; }
    public string LegKey { get; set; } = string.Empty;

    public string FromName { get; set; } = string.Empty;
    public decimal FromLatitude { get; set; }
    public decimal FromLongitude { get; set; }
    public string ToName { get; set; } = string.Empty;
    public decimal ToLatitude { get; set; }
    public decimal ToLongitude { get; set; }
    public string? MeetingPlace { get; set; }

    public Guid DriverTaxiId { get; set; }
    public Guid? DriverRouteId { get; set; }
    public int? ListedFare { get; set; }
    public bool Partial { get; set; }
    public Guid? Backup1TaxiId { get; set; }
    public Guid? Backup2TaxiId { get; set; }

    public Guid? LastEnquiryId { get; set; }
    public string LastKnownStatus { get; set; } = "Unknown";
    public int? LastQuotedFare { get; set; }
    public DateTime? LastKnownAt { get; set; }
    public bool DriverAvailable { get; set; } = true;
    public DateTime UpdatedAt { get; set; }

    public SavedJourney Journey { get; set; } = null!;
}
