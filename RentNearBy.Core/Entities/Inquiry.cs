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
    public Guid? AssignedAgentId { get; set; } // admin sets/changes/removes any time
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public User User { get; set; } = null!;
    public Service Service { get; set; } = null!;
    public ServicePackage ServicePackage { get; set; } = null!;
    public Agent? AssignedAgent { get; set; }
    public ICollection<InquiryStatusHistory> StatusHistory { get; set; } = new List<InquiryStatusHistory>();
}
