using RentNearBy.Core.Models;

namespace RentNearBy.Core.Entities;

public class ServiceCategory
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string IconName { get; set; } = string.Empty;
    public string CoverPhotoUrl { get; set; } = string.Empty;
    public string CoverPhotoFilePath { get; set; } = string.Empty;
    // RentNearBy.Core.Models.ServiceCategoryFormTypes.* — decides which Inquiry Form fields this
    // category's Services/Packages show when a user submits a lead.
    public string FormType { get; set; } = ServiceCategoryFormTypes.Travel;
    // Word shown to consumers instead of "Agent" for this category (e.g. "Travel Expert",
    // "Instructor") — purely cosmetic display text, admin-editable, defaults safely to "Agent".
    public string AgentRoleLabel { get; set; } = "Agent";
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }

    public ICollection<Service> Services { get; set; } = new List<Service>();
    public ICollection<AgentServiceCategory> AgentServiceCategories { get; set; } = new List<AgentServiceCategory>();
}
