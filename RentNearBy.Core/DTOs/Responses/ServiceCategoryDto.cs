namespace RentNearBy.Core.DTOs.Responses;

public class ServiceCategoryDto
{
    public Guid Id { get; set; }
    public Guid ServiceSectionId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string IconName { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
}
