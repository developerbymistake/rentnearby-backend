using System.Text.Json.Serialization;

namespace RentNearBy.Core.DTOs.Responses;

public class StayRoomDto
{
    public Guid Id { get; set; }
    public short RoomIndex { get; set; }
    public int PricePerNight { get; set; }
    public short MaxGuests { get; set; }
    public string RoomName { get; set; } = string.Empty;
    public string Meals { get; set; } = string.Empty;
    public short? Floor { get; set; }
    // Owner roles T2: set on the stay detail only with ?checkIn (per night for his dates, and the base price struck through).
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PriceForDates { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? BasePrice { get; set; }
    // Owner roles M1 (o_st_15): tonight's price with an active special price; owner reads only.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PriceToday { get; set; }
}
