namespace RentNearBy.Core.DTOs.Responses;

// Owner's own stay (GET /stays/me and every owner write). Includes Address; never served publicly.
public class StayOwnerDto
{
    public Guid Id { get; set; }
    public string Status { get; set; } = string.Empty;
    public int Percent { get; set; }
    public bool Step1Done { get; set; }
    public bool RoomsDone { get; set; }
    public bool RulesDone { get; set; }
    public bool Submitted { get; set; }
    public string? Name { get; set; }
    public Guid? StayTypeId { get; set; }
    public string? StayTypeName { get; set; }
    public string? VehicleAccess { get; set; }
    public bool? HostLivesHere { get; set; }
    public List<string> Facilities { get; set; } = new();
    public string? Description { get; set; }
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
    public string? Address { get; set; }
    public Guid? DistrictId { get; set; }
    public string? DistrictName { get; set; }
    public Guid? CityId { get; set; }
    public string? CityName { get; set; }
    public List<StayPhotoDto> Photos { get; set; } = new();
    public List<StayRoomDto> Rooms { get; set; } = new();
    public StayRulesDto? Rules { get; set; }
    public bool AcceptingGuests { get; set; }
    public string? RejectedReason { get; set; }
    public int? MinPricePerNight { get; set; }
    public string? Slug { get; set; }
    public DateTime? SubmittedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public int PendingReportCount { get; set; }   // set by the handler, not mapped
}
