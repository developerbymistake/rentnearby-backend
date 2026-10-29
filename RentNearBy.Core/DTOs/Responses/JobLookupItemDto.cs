namespace RentNearBy.Core.DTOs.Responses;

public class JobLookupItemDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public bool IsActive { get; set; }
    public Guid? JobDepartmentId { get; set; }
}
