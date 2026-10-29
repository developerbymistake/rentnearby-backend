namespace RentNearBy.Core.DTOs.Responses;

public class StayRoomDto
{
    public Guid Id { get; set; }
    public short RoomIndex { get; set; }
    public int PricePerNight { get; set; }
    public short MaxGuests { get; set; }
    public string Bathroom { get; set; } = string.Empty;
    public string Meals { get; set; } = string.Empty;
}
