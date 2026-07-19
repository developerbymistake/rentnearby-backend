namespace RentNearBy.Core.Entities;

// Status is admin-controlled only (user cannot self-cancel): RentNearBy.Core.Models.InquiryStatuses.*
// ServicePackageId is mandatory — no package-less "custom" inquiry.
public class Inquiry
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid ServiceId { get; set; }
    public Guid ServicePackageId { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string Mobile { get; set; } = string.Empty;
    public string? Email { get; set; }
    public DateTime? PreferredDateOrTripStart { get; set; }
    public int? NumberOfPeople { get; set; }
    public string? Message { get; set; }
    public string Status { get; set; } = string.Empty; // RentNearBy.Core.Models.InquiryStatuses.*
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public User User { get; set; } = null!;
    public Service Service { get; set; } = null!;
    public ServicePackage ServicePackage { get; set; } = null!;
    // Many-to-many via InquiryAgent — every active Agent mapped to this Inquiry's ServiceCategory is
    // assigned automatically on creation (or whichever set Admin picks manually), never just one.
    public ICollection<InquiryAgent> InquiryAgents { get; set; } = new List<InquiryAgent>();
    public ICollection<InquiryStatusHistory> StatusHistory { get; set; } = new List<InquiryStatusHistory>();
    public ICollection<InquiryEscalation> Escalations { get; set; } = new List<InquiryEscalation>();
}
