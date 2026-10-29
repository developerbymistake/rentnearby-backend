namespace RentNearBy.Core.DTOs.Responses;

public class StayJobSummaryDto
{
    public Guid Id { get; set; }
    public Guid StayListingId { get; set; }
    public string? StayName { get; set; }
    public string? StayTypeName { get; set; }
    public string? CoverPhotoUrl { get; set; }
    public string Title { get; set; } = string.Empty;
    public string DepartmentName { get; set; } = string.Empty;
    public string RoleName { get; set; } = string.Empty;
    public string ExperienceLevelName { get; set; } = string.Empty;
    public string? JobTypeName { get; set; }
    public string? ShiftTypeName { get; set; }
    public string? SalaryTypeName { get; set; }
    public int? MinimumExperienceYears { get; set; }
    public int MinSalary { get; set; }
    public int MaxSalary { get; set; }
    public Guid? DistrictId { get; set; }
    public string? DistrictName { get; set; }
    public string? CityName { get; set; }
    public DateTime CreatedAt { get; set; }
}
