namespace RentNearBy.Core.DTOs.Responses;

public class StayJobOwnerDto
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? StayName { get; set; }
    public string DepartmentName { get; set; } = string.Empty;
    public string RoleName { get; set; } = string.Empty;
    public int MinimumExperienceYears { get; set; }
    public string SalaryTypeName { get; set; } = string.Empty;
    public int MinSalary { get; set; }
    public int MaxSalary { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? ClosedAt { get; set; }
}
