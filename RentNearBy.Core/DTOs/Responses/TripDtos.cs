using System.Text.Json;
using RentNearBy.Core.Models;

namespace RentNearBy.Core.DTOs.Responses;

public class TripSpotDto
{
    public string Id { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string? Description { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string Category { get; set; } = string.Empty;
    public string? Area { get; set; }
    public string[] AltNames { get; set; } = [];
    public int VisitMinutes { get; set; }
    public bool IsFeatured { get; set; }
    public int Rank { get; set; }
    public string? ImageUrl { get; set; }
    public string? ImageCredit { get; set; }
    public string? ImageLicense { get; set; }
}

public class TripSpotsPageDto
{
    public List<TripSpotDto> Items { get; set; } = new();
    public bool HasMore { get; set; }
    public string? NextCursor { get; set; }
    public string Status { get; set; } = TripSpotStatuses.NotReady;
    public string CityName { get; set; } = string.Empty;
    public long CityKey { get; set; }
    public int Total { get; set; }
    public DateTime? GeneratedAt { get; set; }
}

public class TripPlanResultDto
{
    public List<TripPlanDayDto> Days { get; set; } = new();
    public double? CentreLatitude { get; set; }
    public double? CentreLongitude { get; set; }
    public List<TripWarningDto> Warnings { get; set; } = new();
    public bool OsrmAvailable { get; set; }
    public int DayLimitMinutes { get; set; }
}

public class TripPlanDayDto
{
    public int Day { get; set; }
    public List<string> SpotIds { get; set; } = new();
    public int VisitMinutes { get; set; }
    public int DriveMinutes { get; set; }
    public int TotalMinutes { get; set; }
    public double Km { get; set; }
    public bool OverLimit { get; set; }
    public bool IsEstimate { get; set; }
    public List<TripLegDto> Legs { get; set; } = new();
    // GeoJSON LineString {type, coordinates[[lon,lat],...]} or null.
    public JsonElement? RouteGeoJson { get; set; }
}

public class TripLegDto
{
    public string FromId { get; set; } = string.Empty;
    public string ToId { get; set; } = string.Empty;
    public int Minutes { get; set; }
    public double Km { get; set; }
    public bool IsEstimate { get; set; }
}

public class TripWarningDto
{
    public string Code { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? SpotId { get; set; }
    public int? Day { get; set; }
}

public class TripDto
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Status { get; set; } = TripStatuses.Draft;
    public long CityKey { get; set; }
    public string CityName { get; set; } = string.Empty;
    public double CityLatitude { get; set; }
    public double CityLongitude { get; set; }
    public Guid? DistrictId { get; set; }
    public int DayCount { get; set; }
    public int SpotCount { get; set; }
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public TripPlan Plan { get; set; } = new();
    // Newest first; filled by GET /trips/{id} only.
    public List<TripEnquiryDto> Enquiries { get; set; } = new();
    public string BookingState { get; set; } = TripBookingStates.Draft;
    public DateTime? MutedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class TripSummaryDto
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Status { get; set; } = TripStatuses.Draft;
    public string CityName { get; set; } = string.Empty;
    public int DayCount { get; set; }
    public int SpotCount { get; set; }
    public int TaxiDays { get; set; }
    public int StayDays { get; set; }
    public string? StayName { get; set; }
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public int? Adults { get; set; }
    public int? Kids { get; set; }
    // Category of the first spot, for the card icon.
    public string Category { get; set; } = "attraction";
    public string BookingState { get; set; } = TripBookingStates.Draft;
    public int StaysConfirmed { get; set; }
    public int StaysTotal { get; set; }
    public int TaxisConfirmed { get; set; }
    public int TaxisTotal { get; set; }
    public int StaysOwn { get; set; }
    public int TaxisOwn { get; set; }
    public int AgreedTotal { get; set; }
    public DateTime? MutedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class TripCitySupplyDto
{
    public Guid? DistrictId { get; set; }
    public int Stays { get; set; }
    public int Drivers { get; set; }
}

public class TripListDto
{
    public List<TripSummaryDto> Items { get; set; } = new();
    public bool HasMore { get; set; }
    public TripListCountsDto Counts { get; set; } = new();
}

public class TripCityDto
{
    public long CityKey { get; set; }
    public string Name { get; set; } = string.Empty;
    public string DistrictName { get; set; } = string.Empty;
    public string StateName { get; set; } = string.Empty;
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string? ImageUrl { get; set; }
    public bool IsPopularDestination { get; set; }
    public string Status { get; set; } = TripSpotStatuses.NotReady;
    public int SpotCount { get; set; }
}

public class TripCityPageDto
{
    public List<TripCityDto> Items { get; set; } = new();
    public bool HasMore { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public string Version { get; set; } = string.Empty;
    public int Count { get; set; }
    public bool Stale { get; set; }
}

public class TripCityVersionDto
{
    public string Version { get; set; } = string.Empty;
    public int Count { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool Stale { get; set; }
}
