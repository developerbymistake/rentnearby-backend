namespace RentNearBy.Core.DTOs.Responses;

public class StayJobLookupsDto
{
    public List<JobLookupItemDto> Departments { get; set; } = new();
    public List<JobLookupItemDto> Roles { get; set; } = new();
    public List<JobLookupItemDto> JobTypes { get; set; } = new();
    public List<JobLookupItemDto> ShiftTypes { get; set; } = new();
    public List<JobLookupItemDto> SalaryTypes { get; set; } = new();
}
