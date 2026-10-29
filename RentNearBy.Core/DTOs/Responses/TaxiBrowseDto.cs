namespace RentNearBy.Core.DTOs.Responses;

// List item for the local view and the route search. Never carries contact numbers.
// In search mode MatchedFromName/MatchedToName follow the traveller's direction.
public class TaxiBrowseDto
{
    public Guid Id { get; set; }
    public string? DriverName { get; set; }
    public string? VehicleTypeName { get; set; }
    public string? VehicleCategory { get; set; }
    public short? Seats { get; set; }
    public string? PlateDisplay { get; set; }
    public string? CityName { get; set; }
    public string? DistrictName { get; set; }
    public int? MinFare { get; set; }
    public int RouteCount { get; set; }
    public int? MatchedFare { get; set; }
    public string? MatchedFromName { get; set; }
    public string? MatchedToName { get; set; }
    public string? MatchedTripType { get; set; }
    public bool MatchReversed { get; set; }
    public short PriorityRank { get; set; }
    public bool IsFeatured { get; set; }
    public DateTime CreatedAt { get; set; }
}
