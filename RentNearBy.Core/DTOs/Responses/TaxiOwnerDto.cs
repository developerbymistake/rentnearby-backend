namespace RentNearBy.Core.DTOs.Responses;

// Driver's own taxi (GET /taxis/me and every owner write). Always carries the contact numbers.
public class TaxiOwnerDto
{
    public Guid Id { get; set; }
    public string Status { get; set; } = string.Empty;
    public bool EverApproved { get; set; }
    public int Percent { get; set; }
    public bool Step1Done { get; set; }
    public bool RoutesDone { get; set; }
    public bool Submitted { get; set; }
    public Guid? VehicleTypeId { get; set; }
    public string? VehicleTypeName { get; set; }
    public string? VehicleCategory { get; set; }
    public short? Seats { get; set; }
    public string? NumberPlate { get; set; }
    public string? PlateDisplay { get; set; }
    public string? PlatePhotoUrl { get; set; }
    public string? DisplayPhotoUrl { get; set; }
    public bool PlatePhotoLocked { get; set; }
    public string? ContactPhone { get; set; }
    public string? WhatsAppNumber { get; set; }
    // Only whether they are stored; the values are admin-only (AdminTaxiDto).
    public bool HasDrivingLicence { get; set; }
    public bool HasDateOfBirth { get; set; }
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
    public Guid? DistrictId { get; set; }
    public string? DistrictName { get; set; }
    public Guid? CityId { get; set; }
    public string? CityName { get; set; }
    public List<TaxiRouteDto> Routes { get; set; } = new();
    public bool IsAvailable { get; set; }
    public string? RejectedReason { get; set; }
    public int? MinFare { get; set; }
    public int? LocalDayRate { get; set; }
    public int? LocalDayKm { get; set; }
    public string? Slug { get; set; }
    public DateTime? SubmittedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public int PendingReportCount { get; set; }   // set by the handler, not mapped

    // Owner roles M2 (contract 2.4, AU-9, AU-21, R-10); Routes above stay the visible routes, hidden ones only in HiddenRoutes.
    public List<TaxiRouteDto> HiddenRoutes { get; set; } = new();
    public List<Owner.MeterDto> Meters { get; set; } = new();
    public Owner.MeterDto? RoutesMeter { get; set; }
    public int ActiveBookingCount { get; set; }
    public int RingWeight { get; set; } = 1;
    public bool RemindersOn { get; set; }
    public bool PaymentEnabled { get; set; }
    public bool CanUpgrade { get; set; }
    public bool IsFeatured { get; set; }
    public int ReportCount { get; set; }
}
