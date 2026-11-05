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
    public short? Floor { get; set; }      // 0 = Ground, 3 = "3+"; null = not set
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
