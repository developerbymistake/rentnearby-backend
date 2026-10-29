namespace RentNearBy.Core.DTOs.Responses;

// GET /taxis/{id}. ContactPhone/WhatsAppNumber are nulled for anonymous callers.
public class TaxiPublicDto
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string? Slug { get; set; }
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
    public string? ContactPhone { get; set; }
    public string? WhatsAppNumber { get; set; }
    public bool HasReported { get; set; }
    public DateTime CreatedAt { get; set; }
}
