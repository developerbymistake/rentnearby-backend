namespace RentNearBy.Core.DTOs.Responses;

public class AdminAppVersionConfigDto
{
    public Guid Id { get; set; }
    public string Platform { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public int MinBuildNumber { get; set; }
    public string MinVersionName { get; set; } = string.Empty;
    public int LatestBuildNumber { get; set; }
    public string LatestVersionName { get; set; } = string.Empty;
    public string? StoreUrl { get; set; }
    public string? Message { get; set; }
    public int ForceCacheMaxAgeHours { get; set; }
    public int MaxSeenBuild { get; set; }
    public int FirstGatedBuild { get; set; }
    public Guid? UpdatedByAdminId { get; set; }
    public string? UpdatedByAdminName { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string? Reason { get; set; }
}
