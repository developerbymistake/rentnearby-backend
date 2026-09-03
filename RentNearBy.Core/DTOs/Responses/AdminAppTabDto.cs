namespace RentNearBy.Core.DTOs.Responses;

public class AdminAppTabDto
{
    public Guid Id { get; set; }
    public string TabKey { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public int SortOrder { get; set; }

    // Home/Profile always report true — lets the admin app disable the toggle for these two rows
    // without hardcoding tab keys client-side.
    public bool CanDeactivate { get; set; }
    public Guid? UpdatedByAdminId { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string? Reason { get; set; }
}
