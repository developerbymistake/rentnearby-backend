namespace RentNearBy.Core.Entities;

// Shared shape of the six Jobs lookups; not mapped as an entity of its own.
public abstract class JobLookupBase
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
