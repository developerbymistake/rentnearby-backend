namespace RentNearBy.Core.Entities;

// A job title (Cook, Driver), not a user role.
public class JobRole : JobLookupBase
{
    public Guid JobDepartmentId { get; set; }

    public JobDepartment Department { get; set; } = null!;
}
