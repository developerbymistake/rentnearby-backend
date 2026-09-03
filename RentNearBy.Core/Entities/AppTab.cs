namespace RentNearBy.Core.Entities;

// Master table for the consumer app's bottom-nav tabs (RentNearBy.Core.Models.AppTabKeys). Renaming
// (DisplayName) applies to any row; deactivating (IsActive = false) is only meaningful for the business
// verticals (Rooms/Plots/Services) — Home/Profile are structural and enforced always-on in
// AdminHandlers.UpdateAppTab, not at the entity/DB level.
public class AppTab
{
    public Guid Id { get; set; }
    public string TabKey { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public int SortOrder { get; set; }
    public Guid? UpdatedByAdminId { get; set; }
    public Admin? UpdatedByAdmin { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string? Reason { get; set; }
}
