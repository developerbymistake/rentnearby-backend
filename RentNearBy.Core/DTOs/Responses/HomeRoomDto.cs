namespace RentNearBy.Core.DTOs.Responses;

public class HomeRoomDto
{
    public Guid Id { get; set; }
    public int PriceMonthly { get; set; }
    public string? RoomTypeName { get; set; }
    public string? ThumbnailUrl { get; set; }
    public string? CityName { get; set; }
    public string DistrictName { get; set; } = string.Empty;
    public string FurnishedStatus { get; set; } = "None";
    public DateTime CreatedAt { get; set; }
}
