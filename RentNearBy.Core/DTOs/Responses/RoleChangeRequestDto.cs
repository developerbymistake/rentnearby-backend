namespace RentNearBy.Core.DTOs.Responses;

public class RoleChangeRequestDto
{
    public Guid Id { get; set; }
    public string FromRole { get; set; } = string.Empty;
    public string ToRole { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime RequestedAt { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public string? RejectionReason { get; set; }
    public DateTime? NextRequestAt { get; set; }
}
