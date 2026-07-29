namespace RentNearBy.Core.Entities;

// IconName is duplicated here (also present on ServiceCategory) — Service is the row-level entity for
// the descriptive-list screen, so it needs its own list-row icon. Client falls back to the parent
// Category's icon only when a Service's own IconName is unset.
public class Service
{
    public Guid Id { get; set; }
    public Guid ServiceCategoryId { get; set; }
    public string Name { get; set; } = string.Empty;
    // URL-friendly identifier for the public website (bakhli.com/services/{categorySlug}/{slug}) —
    // generated once from Name at creation time (see RentNearBy.Core.Utils.SlugGenerator) and never
    // changed afterward, even if Name is edited later, so existing links/search-engine indexing stay
    // valid. Unique per-category, not globally (two categories can each have a "premium-package" service).
    public string Slug { get; set; } = string.Empty;
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

    // Service Itinerary fields — all optional, shown on the Service Detail screen for
    // travel/trek-style offerings only (blank/unset for other categories).
    // Terrain the service operates in — drives which itinerary disclaimer text is resolved
    // (RentNearBy.Api.Handlers.ConfigHandlers.ResolveItineraryDisclaimerAsync), e.g. "Hill".
    public string? TerrainType { get; set; }
    // Free-text pickup/drop point shown alongside the itinerary.
    public string? PickupDropLocation { get; set; }
    // Free-text nights breakdown, e.g. "2N Manali, 1N Solang".
    public string? NightsBreakdown { get; set; }
    // Free-text meals-included note, e.g. "Breakfast & Dinner included".
    public string? MealsNote { get; set; }
    // JSON-serialized array of day-wise itinerary entries (List<ItineraryDayDto>) — stored as a
    // single JSON column rather than a child table since itinerary days have no independent
    // lifecycle outside their parent Service.
    public string? ItineraryJson { get; set; }

    public ServiceCategory ServiceCategory { get; set; } = null!;
    public ICollection<ServicePackage> Packages { get; set; } = new List<ServicePackage>();
    public ICollection<AgentService> AgentServices { get; set; } = new List<AgentService>();
}
