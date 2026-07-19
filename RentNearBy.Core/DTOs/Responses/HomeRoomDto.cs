namespace RentNearBy.Core.DTOs.Responses;

public class HomeRoomDto
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; } // the listing owner — lets a browsing client hide its own "Chat" affordance
    public int PriceMonthly { get; set; }
    public string? RoomTypeName { get; set; }
    public string? ThumbnailUrl { get; set; }
    public string? CityName { get; set; }
    public string DistrictName { get; set; } = string.Empty;
    public string FurnishedStatus { get; set; } = "None";
    public DateTime CreatedAt { get; set; }
}
