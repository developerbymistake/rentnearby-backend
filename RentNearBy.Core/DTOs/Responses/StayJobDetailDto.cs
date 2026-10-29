namespace RentNearBy.Core.DTOs.Responses;

public class StayJobDetailDto : StayJobSummaryDto
{
    public string Description { get; set; } = string.Empty;
    public Guid JobDepartmentId { get; set; }
    public Guid JobRoleId { get; set; }
    public Guid JobExperienceLevelId { get; set; }
    public Guid? JobTypeId { get; set; }
    public Guid? JobShiftTypeId { get; set; }
    public Guid? JobSalaryTypeId { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime? ClosedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Guid OwnerUserId { get; set; }
    public bool IsOwner { get; set; }
    public bool HasReportedStay { get; set; }
}
