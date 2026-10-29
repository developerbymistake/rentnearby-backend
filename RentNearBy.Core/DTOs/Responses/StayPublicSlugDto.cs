namespace RentNearBy.Core.DTOs.Responses;

// Anonymous GET /stays/by-slug/{slug}, mirror of PublicRoomListingDto. Deliberately excludes Address,
// OwnerName, OwnerPhone, UserId, HasReported, Status and RejectedReason.
public class StayPublicSlugDto
{
    public Guid Id { get; set; }
    public string Slug { get; set; } = string.Empty;
    public string? Name { get; set; }
    public Guid? StayTypeId { get; set; }
    public string? StayTypeName { get; set; }
    public string? VehicleAccess { get; set; }
    public bool? HostLivesHere { get; set; }
    public List<string> Facilities { get; set; } = new();
    public string? Description { get; set; }
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
    public string? DistrictName { get; set; }
    public string? CityName { get; set; }
    public List<string> Photos { get; set; } = new();
    public List<StayRoomDto> Rooms { get; set; } = new();
    public StayRulesDto? Rules { get; set; }
    public int? MinPricePerNight { get; set; }
    public DateTime CreatedAt { get; set; }
}
