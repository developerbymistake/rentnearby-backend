namespace RentNearBy.Core.DTOs.Requests;

// Price/OriginalPrice/DiscountPercent/IsStartingAtPrice are copied field-for-field from CoinPlan's
// discount-badge logic. Price=null renders "Get Custom Quote"; the admin form's "Get Custom Quote"
// checkbox simply omits/nulls Price+OriginalPrice+DiscountPercent when creating. Thumbnail is uploaded
// separately via PhotoService.SavePackageThumbnailAsync, never as part of this JSON body.
public record CreateServicePackageRequest(
    Guid ServiceId, string Name,
    int? Price, int? OriginalPrice, int? DiscountPercent, bool IsStartingAtPrice,
    int? DurationDays, int? DurationNights, string? PriceUnit,
    int SortOrder = 999, bool IsFeatured = false);
