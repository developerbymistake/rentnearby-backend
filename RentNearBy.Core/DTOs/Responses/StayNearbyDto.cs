namespace RentNearBy.Core.DTOs.Responses;

public class StayNearbyDto
{
    public Guid Id { get; set; }
    public string? Name { get; set; }
    public Guid? StayTypeId { get; set; }
    public string? StayTypeName { get; set; }
    public decimal Latitude { get; set; }
    public decimal Longitude { get; set; }
    public int? MinPricePerNight { get; set; }
    public string? ThumbnailUrl { get; set; }
    public double DistanceKm { get; set; }
    public string? OwnerName { get; set; }
    public string? OwnerPhone { get; set; }
    public bool IsActive { get; set; } = true;
    public short PriorityRank { get; set; }
    public bool IsFeatured { get; set; }
    public string? CityName { get; set; }
    public string? DistrictName { get; set; }
    // Over the stay's rooms: max guests, distinct meal plans joined with " · ", room count; check-in "HH:mm".
    public int? MaxGuests { get; set; }
    public string? Meals { get; set; }
    public string? CheckInTime { get; set; }
    public int RoomCount { get; set; }
    public bool HasGroundFloorRoom { get; set; }
    // The first room (by position) that fits ?guests, else the largest room; set per request.
    public string? BestRoomName { get; set; }
    public int? BestRoomGuests { get; set; }
    public List<StayNearbyRoomDto> Rooms { get; set; } = new();
    // Party quote for ?guests (&rooms, &nights), set per request; null without guests. QuoteTotal = QuotePerNight x nights.
    public bool? Fits { get; set; }
    public int? QuotePerNight { get; set; }
    public int? QuoteTotal { get; set; }
    public List<StayQuoteLineDto>? QuoteLines { get; set; }
}

public class StayNearbyRoomDto
{
    public string RoomName { get; set; } = string.Empty;
    public short MaxGuests { get; set; }
    public short? Floor { get; set; }
    public int? PricePerNight { get; set; }
}

public class StayQuoteLineDto
{
    public string RoomName { get; set; } = string.Empty;
    public int Units { get; set; }
    public int Guests { get; set; }
    public int? PricePerNight { get; set; }
}
