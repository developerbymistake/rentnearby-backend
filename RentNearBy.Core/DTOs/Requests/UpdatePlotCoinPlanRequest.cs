using System.Text.Json.Serialization;

namespace RentNearBy.Core.DTOs.Requests;

public record UpdatePlotCoinPlanRequest(
    int? Days, int? Price,
    [property: JsonPropertyName("plotLimit")] int? ListingLimit,
    bool? IsEnabled, int? OriginalPrice, int? DiscountPercent, bool? IsFeatured);
