namespace RentNearBy.Core.DTOs.Requests;

// Full replace of the stay's rooms (1..2).
public class UpsertStayRoomsRequest
{
    public List<StayRoomInput> Rooms { get; set; } = new();
}

public class StayRoomInput
{
    public Guid? Id { get; set; }
    public int PricePerNight { get; set; }
    public short MaxGuests { get; set; }
    public string RoomName { get; set; } = string.Empty;
    public string Meals { get; set; } = string.Empty;
}
