namespace RentNearBy.Core.DTOs.Responses;

// Public shape (GET /config/app-version) — no audit fields, that's admin-only (see AdminAppVersionConfigDto).
public class AppVersionConfigDto
{
    public string Platform { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public int MinBuildNumber { get; set; }
    public string MinVersionName { get; set; } = string.Empty;
    public int LatestBuildNumber { get; set; }
    public string LatestVersionName { get; set; } = string.Empty;
    public string? StoreUrl { get; set; }
    public string? Message { get; set; }
    public int ForceCacheMaxAgeHours { get; set; }
}
