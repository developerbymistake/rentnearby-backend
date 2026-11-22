using System.Text.Json.Serialization;
using RentNearBy.Core.DTOs.Owner;

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
    // Owner roles T1: cache-only active rules and blocks (contract 8.3); the dated-price pass sets them to null, so no response holds them.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<StayRuleRow>? Rules { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<StayBlockRow>? Blocks { get; set; }
    // Set per request only with ?checkIn (absent otherwise): the price for his dates, the base price and "Full on your dates".
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DatesTotal { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DatesPerNight { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? BasePerNight { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PriceChipDto? PriceChip { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? FullOnDates { get; set; }
    // Dated card offer (StayOfferFields.Dated, only with ?checkIn): offer = DatesPerNight, old = BasePerNight; all three or none.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? OldPricePerNight { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? OfferPricePerNight { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? OfferPercent { get; set; }

    public void SetOffer((int Old, int Offer, int Percent)? offer)
    {
        OldPricePerNight = offer?.Old;
        OfferPricePerNight = offer?.Offer;
        OfferPercent = offer?.Percent;
    }
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
