namespace RentNearBy.Core.DTOs.Responses;

public class ServiceDetailDto
{
    public Guid Id { get; set; }
    public Guid ServiceCategoryId { get; set; }
    // RentNearBy.Core.Models.ServiceCategoryFormTypes.* — the field the Inquiry Form uses to decide
    // which of PreferredDateOrTripStart/NumberOfPeople to show/label.
    public string ServiceCategoryFormType { get; set; } = string.Empty;
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

    // Service Itinerary — populated in the handler after the Adapt call (ItineraryJson is
    // deserialized, ItineraryDisclaimer is resolved via ConfigHandlers), not by Mapster.
    public string? TerrainType { get; set; }
    public string? PickupDropLocation { get; set; }
    public string? NightsBreakdown { get; set; }
    public string? MealsNote { get; set; }
    public List<ItineraryDayDto> ItineraryDays { get; set; } = new();
    public string? ItineraryDisclaimer { get; set; }
}
