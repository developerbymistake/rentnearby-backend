using System.Text.Json.Serialization;
using RentNearBy.Core.DTOs.Owner;

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
    public short PriorityRank { get; set; }
    public bool IsFeatured { get; set; }
    public DateTime CreatedAt { get; set; }
    // Owner roles T3: "N% off" when a live Lower rule runs within the next 30 days; the base FROM price never changes.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PriceChipDto? PriceChip { get; set; }
    // Card offer (StayOfferFields.Undated): all three present or all absent.
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
