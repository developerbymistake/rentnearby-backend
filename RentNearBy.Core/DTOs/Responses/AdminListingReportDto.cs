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
    // to RoomListing/PlotListing) so admin can verify the reported content, not just report metadata.
    public string? Address { get; set; }
    public string? Description { get; set; }
    public string? DistrictName { get; set; }
    public string? CityName { get; set; }

    // Room-only
    public string? RoomTypeName { get; set; }
    public string? FurnishedStatus { get; set; }
    public int? PriceMonthly { get; set; }

    // Plot-only
    public string? PlotType { get; set; }
    public decimal? AreaValue { get; set; }
    public string? AreaUnit { get; set; }
    public decimal? AreaSqft { get; set; }

    // Populated only on GetReportById (single-item detail), left empty on the paginated list —
    // same convention as GoLiveRequestDto.Photos.
    public List<string> Photos { get; set; } = new();
}
