namespace RentNearBy.Core.DTOs.Responses;

public class StayNearbyDto
{
    public Guid Id { get; set; }
    public string? Name { get; set; }
    public Guid? StayTypeId { get; set; }
    public string? StayTypeName { get; set; }
    public decimal Latitude { get; set; }
    public decimal Longitude { get; set; }
    public int? MinPricePerNight { get; set; }
    public string? ThumbnailUrl { get; set; }
    public double DistanceKm { get; set; }
    public string? OwnerName { get; set; }
    public string? OwnerPhone { get; set; }
    public bool IsActive { get; set; } = true;
}
