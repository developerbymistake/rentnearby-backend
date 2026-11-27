namespace RentNearBy.Core.DTOs.Responses;

// Owner's own stay (GET /stays/me and every owner write). Includes Address; never served publicly.
public class StayOwnerDto
{
    public Guid Id { get; set; }
    public string Status { get; set; } = string.Empty;
    public bool EverApproved { get; set; }
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
    // The number and GSTIN are admin-only (AdminStayDto); the owner sees only whether they are stored.
    public Guid? RegistrationTypeId { get; set; }
    public string? RegistrationTypeName { get; set; }
    public string? RegistrationOtherName { get; set; }
    public bool HasRegistrationNumber { get; set; }
    public bool HasGstin { get; set; }
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
    // Share details (UPI ID, WhatsApp): the owner's own values, only ever read back by the owner.
    public string? UpiId { get; set; }
    public string? WhatsAppNumber { get; set; }
    public int PendingReportCount { get; set; }   // set by the handler, not mapped

    // Owner roles M1 (contract 2.4, AU-5, AU-9, AU-21, R-10); filled by the handler on the hub read and the live-edit writes.
    // Rooms and Photos above stay "what I can see and edit"; rows hidden by the plan are only in the two hidden arrays.
    public List<StayRoomDto> HiddenRooms { get; set; } = new();
    public List<Owner.HiddenPhotoDto> HiddenPhotos { get; set; } = new();
    public List<Owner.MeterDto> Meters { get; set; } = new();
    public Owner.StayPendingEditDto? PendingEdit { get; set; }
    public Owner.StayEditResultDto? LastEditResult { get; set; }
    public int ConfirmedBookingCount { get; set; }
    public bool RemindersOn { get; set; }
    // Stay enquiry ring switch "Ring me for new enquiries" (default on).
    public bool EnquiryRingOn { get; set; } = true;
    public bool PaymentEnabled { get; set; }
    public bool CanUpgrade { get; set; }
    public bool IsFeatured { get; set; }
    public int ReportCount { get; set; }
    public Owner.StayCalendarRowDto? CalendarRow { get; set; }
    public Owner.StaySpecialPricesRowDto? SpecialPricesRow { get; set; }
}
