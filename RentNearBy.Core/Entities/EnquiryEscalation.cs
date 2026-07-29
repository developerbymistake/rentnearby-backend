namespace RentNearBy.Core.Entities;

// A consumer's self-service "report an issue with my agent" — resolved by Admin, never seen by the
// assigned agent(s) themselves. Reason is a fixed small set (RentNearBy.Core.Models.EscalationReasons),
// not an admin-CRUD lookup table like ReportReason — see the plan's Design decisions for why.
public class EnquiryEscalation
{
    public Guid Id { get; set; }
    public Guid EnquiryId { get; set; }
    public string Reason { get; set; } = string.Empty; // RentNearBy.Core.Models.EscalationReasons.*
    public string? Note { get; set; }
    public string Status { get; set; } = string.Empty; // "Pending" | "Resolved"
    public DateTime CreatedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public Guid? ResolvedByAdminId { get; set; }

    public Enquiry Enquiry { get; set; } = null!;
    public Admin? ResolvedByAdmin { get; set; }
}
