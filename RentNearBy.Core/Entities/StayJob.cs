namespace RentNearBy.Core.Entities;

// StayListingId and UserId are loose (no FK) so the Jobs module stays removable. Location and the
// stay name/photo are read from the stay at query time.
public class StayJob
{
    public Guid Id { get; set; }
    public Guid StayListingId { get; set; }
    public Guid UserId { get; set; }

    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    public Guid JobDepartmentId { get; set; }
    public Guid JobRoleId { get; set; }
    public Guid JobTypeId { get; set; }
    public Guid JobShiftTypeId { get; set; }
    public Guid JobSalaryTypeId { get; set; }

    public int MinimumExperienceYears { get; set; }   // 0 = Fresher
    public int MinSalary { get; set; }
    public int MaxSalary { get; set; }

    public string ContactPhone { get; set; } = string.Empty;
    public string WhatsAppNumber { get; set; } = string.Empty;

    public string Status { get; set; } = "Active";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? ClosedAt { get; set; }
    // Set once by the seeker push worker so a redelivered message never pushes twice.
    public DateTime? SeekerPushedAt { get; set; }

    public JobDepartment Department { get; set; } = null!;
    public JobRole Role { get; set; } = null!;
    public JobType JobType { get; set; } = null!;
    public JobShiftType ShiftType { get; set; } = null!;
    public JobSalaryType SalaryType { get; set; } = null!;
}
