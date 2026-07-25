namespace RentNearBy.Core.DTOs.Responses;

// Shared shape for both the Room and Plot admin Go-Live request list/detail views (populated from
// either RoomListing or PlotListing by each kind's own mapping helper — mirrors
// AdminListingReportDto being reused by both GetReports and GetReportById). Photos is left empty by
// the list endpoint (no Photos Include there) and populated only by the detail endpoint.
public class GoLiveRequestDto
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string? OwnerName { get; set; }
    public string? OwnerMobile { get; set; }
    public string Status { get; set; } = string.Empty; // GoLiveRequestStatuses.*
    public string? RequestedPlanType { get; set; }
    public int? RequestedPlanDays { get; set; }
    public int? RequestedPlanCreditsSpent { get; set; }
    public DateTime SubmittedAt { get; set; }
    public List<string> Photos { get; set; } = new();
}
