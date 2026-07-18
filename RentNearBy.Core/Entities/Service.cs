namespace RentNearBy.Core.Entities;

// IconName is duplicated here (also present on ServiceCategory) — Service is the row-level entity for
// the descriptive-list screen, so it needs its own list-row icon. Client falls back to the parent
// Category's icon only when a Service's own IconName is unset.
public class Service
{
    public Guid Id { get; set; }
    public Guid ServiceCategoryId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string IconName { get; set; } = string.Empty;
    public string ShortDescription { get; set; } = string.Empty;
    public string FullDescription { get; set; } = string.Empty;
    public string CoverPhotoUrl { get; set; } = string.Empty;
    public string CoverPhotoFilePath { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public bool IsFeatured { get; set; } = false;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ServiceCategory ServiceCategory { get; set; } = null!;
    public ICollection<ServicePackage> Packages { get; set; } = new List<ServicePackage>();
}
