namespace RentNearBy.Core.DTOs.Responses;

// Row shape for the descriptive-list screen (icon+title+one-liner+chevron) and the admin Services list.
public class ServiceListItemDto
{
    public Guid Id { get; set; }
    public Guid ServiceCategoryId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string IconName { get; set; } = string.Empty;
    public string ShortDescription { get; set; } = string.Empty;
    public string CoverPhotoUrl { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public bool IsFeatured { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
}
