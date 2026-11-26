namespace RentNearBy.Core.DTOs.Responses;

public class AdminListingReportDto
{
    public Guid Id { get; set; }
    public Guid ListingId { get; set; }
    public string ListingType { get; set; } = string.Empty;

    public Guid ReporterUserId { get; set; }
    public string ReporterName { get; set; } = string.Empty;
    public string ReporterMobile { get; set; } = string.Empty;

    public Guid ReportedUserId { get; set; }
    public string ReportedName { get; set; } = string.Empty;
    public string ReportedMobile { get; set; } = string.Empty;

    public string ReasonName { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? ResolutionAction { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }

    // Listing details, looked up separately by ListingId+ListingType (ListingReport has no FK/nav
    // to StayListing/TaxiListing) so admin can verify the reported content, not just report metadata.
    public string? Address { get; set; }
    public string? Description { get; set; }
    public string? DistrictName { get; set; }
    public string? CityName { get; set; }

    // Stay-only
    public string? StayName { get; set; }
    public string? StayTypeName { get; set; }
    public int? MinPricePerNight { get; set; }

    // Taxi-only
    public string? TaxiVehicleTypeName { get; set; }
    public string? TaxiPlateDisplay { get; set; }
    public short? TaxiSeats { get; set; }

    // Trip enquiry context: set on stay/taxi reports filed from a trip enquiry and on every TripEnquiry (reported traveller) report.
    public Guid? TripEnquiryId { get; set; }
    public string? TripEnquiryKind { get; set; }
    public string? TripEnquiryDates { get; set; }
    public string? TripEnquiryStatus { get; set; }

    // Populated only on GetReportById (single-item detail), left empty on the paginated list.
    public List<string> Photos { get; set; } = new();

    // Person reports (ListingType "User"): buddy feature.
    public string? ReportedUsername { get; set; }
    public string? Context { get; set; }
    public bool? ReportedUserActive { get; set; }
    public DateTime? BuddyRequestsHeldUntil { get; set; }
    public RentNearBy.Core.DTOs.Buddy.PersonReportCountsDto? Counts { get; set; }

    // Bakhli social: ListingType Message carries the reported seq; a snapshot of the chat may be attached; TripPost the post.
    public long? MessageSeq { get; set; }
    public bool HasSnapshot { get; set; }
    public RentNearBy.Core.DTOs.Social.ReportTripPostDto? TripPost { get; set; }
}
