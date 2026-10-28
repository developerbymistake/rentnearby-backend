namespace RentNearBy.Core.DTOs.Requests;

public record UpdateAppVersionConfigRequest(
    bool? IsEnabled,
    int? MinBuildNumber,
    string? MinVersionName,
    int? LatestBuildNumber,
    string? LatestVersionName,
    string? StoreUrl,
    string? Message,
    int? ForceCacheMaxAgeHours,
    string? Reason,
    bool? ConfirmUnseenBuild);
