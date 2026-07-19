namespace RentNearBy.Core.DTOs.Responses;

public class ServiceDetailDto
{
    public Guid Id { get; set; }
    public Guid ServiceCategoryId { get; set; }
    // RentNearBy.Core.Models.ServiceCategoryFormTypes.* — the field the Inquiry Form uses to decide
    // which of PreferredDateOrTripStart/NumberOfPeople to show/label.
    public string ServiceCategoryFormType { get; set; } = string.Empty;
    public Guid ServiceSectionId { get; set; }
    public string ServiceSectionName { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string IconName { get; set; } = string.Empty;
    public string ShortDescription { get; set; } = string.Empty;
    public string FullDescription { get; set; } = string.Empty;
    public string CoverPhotoUrl { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public bool IsFeatured { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public List<ServicePackagePreviewDto> Packages { get; set; } = new();
}
