namespace RentNearBy.Core.DTOs.Responses;

public class EnquiryStatusHistoryDto
{
    public Guid Id { get; set; }
    public Guid EnquiryId { get; set; }
    public string Status { get; set; } = string.Empty;
    public Guid? ChangedByAdminId { get; set; }
    public string? ChangedByAdminName { get; set; }
    public Guid? ChangedByAgentId { get; set; }
    public string? ChangedByAgentName { get; set; }
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; }
}
