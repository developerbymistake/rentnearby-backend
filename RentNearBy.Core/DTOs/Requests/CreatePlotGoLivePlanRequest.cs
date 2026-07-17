using System.Text.Json.Serialization;

namespace RentNearBy.Core.DTOs.Requests;

public record CreatePlotGoLivePlanRequest(
    string PlanType, int Price, int Days,
    [property: JsonPropertyName("plotLimit")] int ListingLimit,
    int OriginalPrice = 0, int DiscountPercent = 0, bool IsFeatured = false);
