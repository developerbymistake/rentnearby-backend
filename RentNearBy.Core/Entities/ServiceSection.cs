namespace RentNearBy.Core.Entities;

// Top-level vertical grouping (e.g. "Explore Uttarakhand", "Expert Consultations"). Pure admin-CRUD
// data — no code ever branches on a specific Section, so a 3rd vertical needs zero code changes.
public class ServiceSection
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string IconName { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }

    public ICollection<ServiceCategory> Categories { get; set; } = new List<ServiceCategory>();
}
