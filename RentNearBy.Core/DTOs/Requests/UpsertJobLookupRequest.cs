namespace RentNearBy.Core.DTOs.Requests;

public class UpsertJobLookupRequest
{
    public string Name { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    // Roles only.
    public Guid? JobDepartmentId { get; set; }
}
