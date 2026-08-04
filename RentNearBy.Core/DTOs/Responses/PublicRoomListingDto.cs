namespace RentNearBy.Core.DTOs.Responses;

// Anonymous, no-auth response shape for GET /listings/by-slug/{slug} — consumed by both the public
// website (bakhli.com/l/r/{slug}) and the Flutter app's deep-link resolver (which then re-uses the
// returned Id against the existing, auth-aware GET /listings/{id} flow rather than needing phone
// redaction logic duplicated here). Deliberately excludes Address, OwnerName, OwnerPhone, UserId,
// HasReported, PendingReportCount, LiveRequestStatus, RejectedReason — none of which belong in an
// anonymous external response (see RoomListingDto, the authenticated counterpart, for the full field set).
public class PublicRoomListingDto
{
    public Guid Id { get; set; }
    public string Slug { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int PriceMonthly { get; set; }
    public decimal Latitude { get; set; }
    public decimal Longitude { get; set; }
    public string? DistrictName { get; set; }
    public string? RoomTypeName { get; set; }
    public string? CityName { get; set; }
    public string FurnishedStatus { get; set; } = "None";
    public List<string> Photos { get; set; } = new();
}
