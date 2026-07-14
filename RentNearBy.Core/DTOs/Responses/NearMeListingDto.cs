namespace RentNearBy.Core.DTOs.Responses;

public class NearMeListingDto
{
    public Guid Id { get; set; }
    public int? PriceMonthly { get; set; }
    public decimal Latitude { get; set; }
    public decimal Longitude { get; set; }
    public Guid RoomTypeId { get; set; }
    public string? RoomTypeName { get; set; }
    public string? OwnerName { get; set; }
    public string? OwnerPhone { get; set; }
    public string? ThumbnailUrl { get; set; }
    public double DistanceKm { get; set; }
    public bool IsActive { get; set; }
    public string FurnishedStatus { get; set; } = "None";
}
