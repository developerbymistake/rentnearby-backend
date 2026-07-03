namespace RentNearBy.Core.DTOs.Responses;

public class AdminListingDto
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string? OwnerName { get; set; }
    public string? OwnerPhone { get; set; }
    public string? DistrictName { get; set; }
    public string? CityName { get; set; }
    public string? RoomTypeName { get; set; }
    public int PriceMonthly { get; set; }
    public bool IsActive { get; set; }
    public string FurnishedStatus { get; set; } = "None";
    public string? Address { get; set; }
    public string? ThumbnailUrl { get; set; }
    public int PhotoCount { get; set; }
    public DateTime CreatedAt { get; set; }
}
