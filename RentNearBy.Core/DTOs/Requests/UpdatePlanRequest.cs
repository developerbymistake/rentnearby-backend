namespace RentNearBy.Core.DTOs.Requests;

public record UpdatePlanRequest(int? Days, int? Price, int? RoomLimit, bool? IsEnabled, int? OriginalPrice, int? DiscountPercent);
