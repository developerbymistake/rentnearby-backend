namespace RentNearBy.Core.DTOs.Responses;

// ServiceSectionId/Name are resolved through the Service -> ServiceCategory -> ServiceSection nav
// chain — used for the consumer "My Inquiries" small section badge per row and the admin
// status/section filter chips.
public class InquiryListItemDto
{
    public Guid Id { get; set; }
    public Guid ServiceId { get; set; }
    public string ServiceName { get; set; } = string.Empty;
    public Guid ServiceSectionId { get; set; }
    public string ServiceSectionName { get; set; } = string.Empty;
    public Guid ServicePackageId { get; set; }
    public string ServicePackageName { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string Mobile { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public Guid? AssignedAgentId { get; set; }
    public string? AssignedAgentName { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
