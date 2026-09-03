namespace RentNearBy.Core.DTOs.Responses;

// Public shape (GET /config/app-tabs) — no audit fields, that's admin-only (see AdminAppTabDto).
public class AppTabDto
{
    public string TabKey { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public int SortOrder { get; set; }
}
