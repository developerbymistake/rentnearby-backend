namespace RentNearBy.Core.DTOs.Responses;

public class ServiceCategoryDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string IconName { get; set; } = string.Empty;
    public string CoverPhotoUrl { get; set; } = string.Empty;
    public string FormType { get; set; } = string.Empty;
    public string AgentRoleLabel { get; set; } = "Agent";
    public int SortOrder { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
}
