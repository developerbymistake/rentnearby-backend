namespace RentNearBy.Core.DTOs.Responses;

// ServiceCategoryId/Name are resolved through the Service -> ServiceCategory nav chain — used for
// the consumer "My Inquiries" small category badge per row and the admin status/category filter
// chips.
public class InquiryListItemDto
{
    public Guid Id { get; set; }
    public Guid ServiceId { get; set; }
    public string ServiceName { get; set; } = string.Empty;
    public Guid ServiceCategoryId { get; set; }
    public string ServiceCategoryName { get; set; } = string.Empty;
    // Word to show instead of "Agent" for this category (e.g. "Travel Expert", "Instructor") —
    // admin-editable on ServiceCategory, resolved through the same Service -> ServiceCategory chain.
    public string ServiceCategoryAgentRoleLabel { get; set; } = "Agent";
    public Guid ServicePackageId { get; set; }
    public string ServicePackageName { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string Mobile { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    // Multiple Agents can be assigned simultaneously — 0 means unassigned, list-view rows only need
    // the count; full names are only shown on the Detail screen (AssignedAgents there).
    public int AssignedAgentCount { get; set; }
    // True while a consumer-filed "report an issue with my agent" is awaiting Admin review — powers
    // the admin list's flag chip/filter. Always false for the consumer's own My Inquiries rows use
    // (not rendered there), but present since InquiryListItemDto is the shared shape.
    public bool HasPendingEscalation { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
