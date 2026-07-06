namespace RentNearBy.Core.Entities;

public class ListingReport
{
    public Guid Id { get; set; }
    public Guid ListingId { get; set; }
    public string ListingType { get; set; } = string.Empty; // "Room" | "Plot"

    public Guid ReporterUserId { get; set; }
    public string ReporterName { get; set; } = string.Empty;
    public string ReporterMobile { get; set; } = string.Empty;

    public Guid ReportedUserId { get; set; }
    public string ReportedName { get; set; } = string.Empty;
    public string ReportedMobile { get; set; } = string.Empty;

    public Guid ReasonId { get; set; }
    public ReportReason? Reason { get; set; }

    public string Details { get; set; } = string.Empty;
    public string Status { get; set; } = "Pending"; // "Pending" | "Reviewed"
    public string? ResolutionAction { get; set; } // "PostDeactivated" | "PostDeleted" | "AccountDeactivated" | "Dismissed" | "AutoResolvedByOwner"

    public DateTime CreatedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public Guid? ResolvedByAdminId { get; set; }
    public Admin? ResolvedByAdmin { get; set; }
}
