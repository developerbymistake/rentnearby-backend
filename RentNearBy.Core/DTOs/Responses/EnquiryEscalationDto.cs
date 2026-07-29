namespace RentNearBy.Core.DTOs.Responses;

public class EnquiryEscalationDto
{
    public Guid Id { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string? Note { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
}
