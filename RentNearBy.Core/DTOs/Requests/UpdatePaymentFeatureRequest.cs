namespace RentNearBy.Core.DTOs.Requests;

public record UpdatePaymentFeatureRequest(bool? IsEnabled, int? FreeDurationDays, string? Reason);
