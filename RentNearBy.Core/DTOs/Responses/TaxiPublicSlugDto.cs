namespace RentNearBy.Core.DTOs.Responses;

// Anonymous GET /taxis/by-slug/{slug}: no contact numbers, no HasReported.
public class TaxiPublicSlugDto
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Slug { get; set; } = string.Empty;
    public string? DriverName { get; set; }
    public string? VehicleTypeName { get; set; }
    public string? VehicleCategory { get; set; }
    public short? Seats { get; set; }
    public string? NumberPlate { get; set; }
    public string? PlateDisplay { get; set; }
    public string? PlatePhotoUrl { get; set; }
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
    public string? DistrictName { get; set; }
    public string? CityName { get; set; }
    public List<TaxiRouteDto> Routes { get; set; } = new();
    public int? MinFare { get; set; }
    public bool IsAvailable { get; set; }
    public DateTime CreatedAt { get; set; }
}
