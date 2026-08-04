namespace RentNearBy.Core.DTOs.Responses;

// Anonymous, no-auth response shape for GET /plots/by-slug/{slug} — same reasoning as
// PublicRoomListingDto (see that file's doc comment). Deliberately excludes OwnerPhone, UserId,
// HasReported, PendingReportCount, LiveRequestStatus, RejectedReason.
public class PublicPlotListingDto
{
    public Guid Id { get; set; }
    public string Slug { get; set; } = string.Empty;
    public decimal AreaValue { get; set; }
    public string AreaUnit { get; set; } = string.Empty;
    public string PlotType { get; set; } = string.Empty;
    public string? Description { get; set; }
    public decimal Latitude { get; set; }
    public decimal Longitude { get; set; }
    public string? Address { get; set; }
    public string? DistrictName { get; set; }
    public string? CityName { get; set; }
    public string? OwnerName { get; set; }
    public List<string> Photos { get; set; } = new();
}
