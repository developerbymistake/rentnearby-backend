namespace RentNearBy.Core.DTOs.Responses;

public class ListingReportDto
{
    public Guid Id { get; set; }
    public Guid ListingId { get; set; }
    public string ListingType { get; set; } = string.Empty;

    public string ReasonName { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? ResolutionAction { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
}
