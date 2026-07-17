using System.Text.Json.Serialization;

namespace RentNearBy.Core.DTOs.Requests;

public record UpdateRoomGoLivePlanRequest(
    int? Days, int? Price,
    [property: JsonPropertyName("roomLimit")] int? ListingLimit,
    bool? IsEnabled, int? OriginalPrice, int? DiscountPercent, bool? IsFeatured);
