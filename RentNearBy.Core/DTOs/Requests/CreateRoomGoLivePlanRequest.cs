using System.Text.Json.Serialization;

namespace RentNearBy.Core.DTOs.Requests;

public record CreateRoomGoLivePlanRequest(
    string PlanType, int Price, int Days,
    [property: JsonPropertyName("roomLimit")] int ListingLimit,
    int OriginalPrice = 0, int DiscountPercent = 0, bool IsFeatured = false);
