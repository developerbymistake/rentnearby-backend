namespace RentNearBy.Core.DTOs.Requests;

// FreeModeListingLimit null = unchanged; ClearFreeModeListingLimit resets it to the default plan's limit.
public record UpdatePaymentFeatureRequest(
    bool? IsEnabled,
    int? FreeDurationDays,
    string? Reason,
    int? FreeModeListingLimit = null,
    bool ClearFreeModeListingLimit = false);
