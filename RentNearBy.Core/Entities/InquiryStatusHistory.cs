namespace RentNearBy.Core.Entities;

// Append-only ledger, mirrors CoinTransaction's shape exactly.
public class InquiryStatusHistory
{
    public Guid Id { get; set; }
    public Guid InquiryId { get; set; }
    public string Status { get; set; } = string.Empty; // RentNearBy.Core.Models.InquiryStatuses.*
    public Guid? ChangedByAdminId { get; set; }
    // A dedicated field, not a repurposed ChangedByAdminId — an Agent-driven status change is not
    // an Admin-driven one, even though both are "staff" in a loose sense. Exactly one of
    // ChangedByAdminId/ChangedByAgentId is ever set (or neither, for the initial Submitted entry).
    public Guid? ChangedByAgentId { get; set; }
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; }

    public Inquiry Inquiry { get; set; } = null!;
    public Admin? ChangedByAdmin { get; set; }
    public Agent? ChangedByAgent { get; set; }
}
