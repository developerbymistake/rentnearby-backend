using System.Text.Json.Serialization;
using RentNearBy.Core.DTOs.Owner;

namespace RentNearBy.Core.DTOs.Responses;

// Anonymous GET /stays/by-slug/{slug}. Deliberately excludes Address,
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
    public Guid? CityId { get; set; }
    public string? CityName { get; set; }
    public decimal? CityLatitude { get; set; }
    public decimal? CityLongitude { get; set; }
    public List<string> Photos { get; set; } = new();
    public List<StayRoomDto> Rooms { get; set; } = new();
    public StayRulesDto? Rules { get; set; }
    public int? MinPricePerNight { get; set; }
    public DateTime CreatedAt { get; set; }
    // Owner roles T2: only with ?checkIn. The lowest room price for his dates, the chip "N% off" and "Full on your dates".
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DatesFromPerNight { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PriceChipDto? PriceChip { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? FullOnDates { get; set; }
}
