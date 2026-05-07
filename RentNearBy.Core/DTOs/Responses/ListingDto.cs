namespace RentNearBy.Core.DTOs.Responses;

public class ListingDto
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string? Title { get; set; }
    public string? Description { get; set; }
    public int? PriceMonthly { get; set; }
    public int? PricePerDay { get; set; }
    public decimal Latitude { get; set; }
    public decimal Longitude { get; set; }
    public string? Address { get; set; }
    public Guid DistrictId { get; set; }
    public string? DistrictName { get; set; }
    public Guid RoomTypeId { get; set; }
    public string? RoomTypeName { get; set; }
    public Guid? CityId { get; set; }
    public string? CityName { get; set; }
    public bool IsActive { get; set; }
    public string? OwnerName { get; set; }
    public string? OwnerPhone { get; set; }
    public List<string> Photos { get; set; } = new();
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
