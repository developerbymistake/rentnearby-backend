namespace RentNearBy.Core.DTOs.Responses;

public class InquiryDetailDto
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid ServiceId { get; set; }
    public string ServiceName { get; set; } = string.Empty;
    public Guid ServiceSectionId { get; set; }
    public string ServiceSectionName { get; set; } = string.Empty;
    public Guid ServicePackageId { get; set; }
    public string ServicePackageName { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string Mobile { get; set; } = string.Empty;
    public string? Email { get; set; }
    public DateTime? PreferredDateOrTripStart { get; set; }
    public int? NumberOfPeople { get; set; }
    public string? Message { get; set; }
    public string Status { get; set; } = string.Empty;
    // Every Agent currently assigned — never null, empty when unassigned. An Inquiry can have
    // multiple simultaneous Agents (see InquiryAgent).
    public List<AssignedAgentDto> AssignedAgents { get; set; } = new();
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public List<InquiryStatusHistoryDto> StatusHistory { get; set; } = new();
}
