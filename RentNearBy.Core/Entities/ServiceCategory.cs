namespace RentNearBy.Core.Entities;

public class ServiceCategory
{
    public Guid Id { get; set; }
    public Guid ServiceSectionId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string IconName { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }

    public ServiceSection ServiceSection { get; set; } = null!;
    public ICollection<Service> Services { get; set; } = new List<Service>();
    public ICollection<AgentServiceCategory> AgentServiceCategories { get; set; } = new List<AgentServiceCategory>();
}
