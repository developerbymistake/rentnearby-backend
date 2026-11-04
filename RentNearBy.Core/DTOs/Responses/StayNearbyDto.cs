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
    public short PriorityRank { get; set; }
    public bool IsFeatured { get; set; }
    // Over the stay's rooms: max guests, distinct meal plans joined with " · ", room count; check-in "HH:mm".
    public int? MaxGuests { get; set; }
    public string? Meals { get; set; }
    public string? CheckInTime { get; set; }
    public int RoomCount { get; set; }
}
