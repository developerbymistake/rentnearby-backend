namespace RentNearBy.Core.DTOs.Requests;

// FreeModeListingLimit/Provider null = unchanged; ClearFreeModeListingLimit resets it to the default plan's limit.
public record UpdatePaymentFeatureRequest(
    bool? IsEnabled,
    int? FreeDurationDays,
    string? Reason,
    int? FreeModeListingLimit = null,
    bool ClearFreeModeListingLimit = false,
    string? Provider = null);
