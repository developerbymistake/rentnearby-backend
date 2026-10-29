namespace RentNearBy.Core.DTOs.Requests;

public class UpsertStayJobRequest
{
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public Guid JobDepartmentId { get; set; }
    public Guid JobRoleId { get; set; }
    public Guid JobExperienceLevelId { get; set; }
    public Guid? JobTypeId { get; set; }
    public Guid? JobShiftTypeId { get; set; }
    public Guid? JobSalaryTypeId { get; set; }
    public int? MinimumExperienceYears { get; set; }
    public int MinSalary { get; set; }
    public int MaxSalary { get; set; }
}
