namespace RentNearBy.Core.Entities;

// Fixed master list of service inclusions (e.g. "Hotel Stay", "Meals Included"), admin-managed.
public class Inclusion
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string IconName { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;

    public ICollection<ServiceInclusion> ServiceInclusions { get; set; } = new List<ServiceInclusion>();
}
