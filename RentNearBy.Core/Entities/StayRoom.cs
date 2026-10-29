namespace RentNearBy.Core.Entities;

public class StayRoom
{
    public Guid Id { get; set; }
    public Guid StayListingId { get; set; }
    public short RoomIndex { get; set; }
    public int PricePerNight { get; set; }
    public short MaxGuests { get; set; }   // 4 = "4+"
    public string RoomName { get; set; } = string.Empty;
    public string Meals { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
