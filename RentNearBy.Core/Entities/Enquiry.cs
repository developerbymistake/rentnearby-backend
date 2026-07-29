namespace RentNearBy.Core.Entities;

// Status is admin-controlled only (user cannot self-cancel): RentNearBy.Core.Models.EnquiryStatuses.*
// ServicePackageId is mandatory — no package-less "custom" enquiry.
public class Enquiry
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
    public string Status { get; set; } = string.Empty; // RentNearBy.Core.Models.EnquiryStatuses.*
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    // Enquiry is single-owner (one consumer), so the consumer's seen-timestamp lives directly here.
    public DateTime? UserSeenAt { get; set; }

    public Service Service { get; set; } = null!;
    public ServicePackage ServicePackage { get; set; } = null!;
    // Many-to-many via EnquiryAgent — every active Agent mapped to this Enquiry's ServiceCategory is
    // assigned automatically on creation (or whichever set Admin picks manually), never just one.
    public ICollection<EnquiryAgent> EnquiryAgents { get; set; } = new List<EnquiryAgent>();
    public ICollection<EnquiryStatusHistory> StatusHistory { get; set; } = new List<EnquiryStatusHistory>();
    public ICollection<EnquiryEscalation> Escalations { get; set; } = new List<EnquiryEscalation>();
}
