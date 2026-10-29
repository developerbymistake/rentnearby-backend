namespace RentNearBy.Core.DTOs.Responses;

public class StayBrowseDto
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string? Name { get; set; }
    public Guid? StayTypeId { get; set; }
    public string? StayTypeName { get; set; }
    public string? ThumbnailUrl { get; set; }
    public string? CityName { get; set; }
    public string? DistrictName { get; set; }
    public int? MinPricePerNight { get; set; }
    public string? MealsChip { get; set; }      // best meals across rooms: AllMeals > Breakfast > null
    public string? RoomNameChip { get; set; }   // name of the cheapest room
    public DateTime CreatedAt { get; set; }
}
